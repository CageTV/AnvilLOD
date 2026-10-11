#include "LodLayers.h"

#include "IniFile.h"

namespace AnvilLOD
{
	namespace
	{
		constexpr const char* kNames[Layers::kLayerCount] = { "Grass", "3D trees", "Tree cards" };
		constexpr const char* kKeys[Layers::kLayerCount] = { "grass", "tree3d", "treecard" };
		constexpr int         kLevels[Layers::kLevelCount] = { 4, 8, 16, 32 };

		std::string KeyFor(int a_layer, int a_level) { return std::format("bShow_{}_{}", kKeys[a_layer], kLevels[a_level]); }

		/// "AL:grass:16" -> layer 0, level index 2. False for anything else.
		bool Parse(const char* a_name, int& a_layer, int& a_level)
		{
			if (!a_name || a_name[0] != 'A' || a_name[1] != 'L' || a_name[2] != ':') {
				return false;
			}
			const std::string_view rest{ a_name + 3 };
			const auto             colon = rest.find(':');
			if (colon == std::string_view::npos) {
				return false;
			}
			a_layer = -1;
			for (int i = 0; i < Layers::kLayerCount; ++i) {
				if (rest.substr(0, colon) == kKeys[i]) a_layer = i;
			}
			a_level = -1;
			const auto lv = rest.substr(colon + 1);
			for (int i = 0; i < Layers::kLevelCount; ++i) {
				if (lv == std::to_string(kLevels[i])) a_level = i;
			}
			return a_layer >= 0 && a_level >= 0;
		}

		std::string PathTo(const RE::NiAVObject* a_object)
		{
			std::string out;
			for (auto* o = a_object; o; o = o->parent) {
				const char* n = o->name.c_str();
				out = std::format("{}{}{}", (n && *n) ? n : "<unnamed>", out.empty() ? "" : " < ", out);
			}
			return out;
		}
	}

	Layers& Layers::Get()
	{
		static Layers instance;
		return instance;
	}

	const char* Layers::LayerName(int a_layer) { return kNames[a_layer]; }
	const char* Layers::LayerKey(int a_layer) { return kKeys[a_layer]; }
	int         Layers::LevelNumber(int a_level) { return kLevels[a_level]; }

	void Layers::Load()
	{
		std::ifstream in{ std::filesystem::path{ "Data/SKSE/Plugins/AnvilLOD.ini" } };
		if (!in) {
			return;
		}
		auto trim = [](const std::string& s) {
			const auto a = s.find_first_not_of(" \t\r\n");
			if (a == std::string::npos) return std::string{};
			return s.substr(a, s.find_last_not_of(" \t\r\n") - a + 1);
		};
		std::string line;
		while (std::getline(in, line)) {
			const auto eq = line.find('=');
			if (eq == std::string::npos || line.empty() || line[0] == ';' || line[0] == '#' || line[0] == '[') {
				continue;
			}
			const auto key = trim(line.substr(0, eq));
			const auto value = trim(line.substr(eq + 1));
			for (int l = 0; l < kLayerCount; ++l) {
				for (int v = 0; v < kLevelCount; ++v) {
					if (_stricmp(key.c_str(), KeyFor(l, v).c_str()) == 0) {
						show[l][v] = value != "0" && _stricmp(value.c_str(), "false") != 0;
					}
				}
			}
		}
		logger::info("Quality layers: {}", AnyHidden() ? "some layers are hidden (AnvilLOD.ini [Layers])" : "everything shown");
	}

	bool Layers::Save() const
	{
		std::vector<std::pair<std::string, std::string>> values;
		for (int l = 0; l < kLayerCount; ++l) {
			for (int v = 0; v < kLevelCount; ++v) {
				values.emplace_back(KeyFor(l, v), show[l][v] ? "1" : "0");
			}
		}
		return IniFile::Write("Data/SKSE/Plugins/AnvilLOD.ini", "Layers", values);
	}

	bool Layers::AnyHidden() const
	{
		for (const auto& row : show) {
			for (bool v : row) {
				if (!v) return true;
			}
		}
		return false;
	}

	void Layers::Preset(int a_preset)
	{
		for (auto& row : show) {
			for (auto& v : row) v = true;
		}
		if (a_preset >= 1) {  // High: the 3D trees only where they are close
			show[kTree3D][1] = false;
			show[kTree3D][2] = false;
			show[kTree3D][3] = false;
		}
		if (a_preset >= 2) {  // Medium: grass only in the nearest two levels
			show[kGrass][2] = false;
			show[kGrass][3] = false;
		}
		if (a_preset >= 3) {  // Low: grass only at LOD4
			show[kGrass][1] = false;
		}
		Changed();
	}

	void Layers::Start()
	{
		if (_running.exchange(true)) {
			return;
		}
		std::thread([this] {
			const std::filesystem::path ini{ "Data/SKSE/Plugins/AnvilLOD.ini" };
			std::error_code             ec;
			auto                        stamp = std::filesystem::last_write_time(ini, ec);
			for (;;) {
				std::this_thread::sleep_for(500ms);
				// Editing [Layers] in AnvilLOD.ini while the game runs applies within half a second (also how it is tested).
				if (const auto now = std::filesystem::last_write_time(ini, ec); !ec && now != stamp) {
					stamp = now;
					Load();
					_dirty = true;
				}
				if (!_dirty && !AnyHidden() && !_appliedHidden) {
					continue;  // everything shown and nothing to undo: no scene graph work at all
				}
				if (_queued.exchange(true)) {
					continue;
				}
				SKSE::GetTaskInterface()->AddTask([this] {
					Apply();
					_queued = false;
				});
			}
		}).detach();
	}

	void Layers::Apply()
	{
		auto* tes = RE::TES::GetSingleton();
		if (!tes || !tes->objRoot) {
			static bool noRoot = false;
			if (!noRoot) {
				noRoot = true;
				logger::info("Quality layers: no world scene yet (TES objRoot missing); will look again");
			}
			return;
		}
		_dirty = false;
		++_scans;
		const bool hidden = AnyHidden();

		// The whole scene graph is walked (LOD blocks hang somewhere under the world root; their exact parent is not
		// the same in every runtime), but never below a layer node. Hidden layers rescan every tick so blocks that
		// load later are caught within half a second; with everything shown this only runs after a change.
		RE::NiAVObject* top = tes->objRoot;
		while (top->parent) {
			top = top->parent;
		}
		// Seen in game: the loaded blocks hang under "LODRoot" (WorldRoot Node < shadow scene node < LODRoot < LandLOD).
		// Once found, only that subtree is walked, which is a small fraction of the scene. Without it, walk everything.
		if (_lodRoot && !_lodRoot->parent) {
			_lodRoot.reset();
		}
		if (!_lodRoot) {
			std::vector<std::pair<RE::NiAVObject*, int>> probe{ { top, 0 } };
			while (!probe.empty() && !_lodRoot) {
				auto [o, depth] = probe.back();
				probe.pop_back();
				if (const char* nm = o->name.c_str(); nm && _stricmp(nm, "LODRoot") == 0) {
					_lodRoot = RE::NiPointer<RE::NiNode>(o->AsNode());
					break;
				}
				if (depth < 4) {
					if (auto* node = o->AsNode()) {
						for (auto& child : node->GetChildren()) {
							if (child) probe.emplace_back(child.get(), depth + 1);
						}
					}
				}
			}
			if (_lodRoot) {
				logger::info("Quality layers: LODRoot found at {}; only that subtree is scanned from now on", PathTo(_lodRoot.get()));
			}
		}
		if (_lodRoot) {
			top = _lodRoot.get();
		}
		int                          found[kLayerCount][kLevelCount]{};
		std::size_t                  visited = 0;
		std::vector<RE::NiAVObject*> stack{ top };
		while (!stack.empty() && visited < 600000) {
			auto* o = stack.back();
			stack.pop_back();
			++visited;
			int layer = 0, level = 0;
			if (const char* nm = o->name.c_str(); nm && !_objLogged && _stricmp(nm, "obj") == 0) {
				// The root of a loaded .bto block is named "obj": tells where the LOD blocks hang in the scene.
				_objLogged = true;
				logger::info("Quality layers: a block root 'obj' sits at: {}", PathTo(o));
			}
			if (Parse(o->name.c_str(), layer, level)) {
				++found[layer][level];
				const bool cull = !show[layer][level];
				if (o->GetAppCulled() != cull) {
					o->SetAppCulled(cull);
				}
				if (!_logged) {
					_logged = true;
					logger::info("Quality layers: first layer node found: {}", PathTo(o));
				}
				continue;
			}
			if (auto* node = o->AsNode()) {
				for (auto& child : node->GetChildren()) {
					if (child) stack.push_back(child.get());
				}
			}
		}

		bool changed = false;
		bool any = false;
		for (int l = 0; l < kLayerCount; ++l) {
			for (int v = 0; v < kLevelCount; ++v) {
				changed |= _found[l][v] != found[l][v];
				_found[l][v] = found[l][v];
				any |= found[l][v] > 0;
			}
		}
		_anyFound = any;
		_appliedHidden = hidden;
		if (changed || _scans <= 3) {
			logger::info("Quality layers: scan {}: scene graph walked ({} nodes). Layer nodes found: grass {}/{}/{}/{}, 3D trees {}/{}/{}/{}, tree cards {}/{}/{}/{} (LOD4/8/16/32)", _scans, visited,
				found[0][0], found[0][1], found[0][2], found[0][3], found[1][0], found[1][1], found[1][2], found[1][3], found[2][0], found[2][1], found[2][2], found[2][3]);
		}
	}
}
