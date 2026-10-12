#include "Controller.h"
#include "Settings.h"

namespace AnvilLOD
{
	namespace
	{
		constexpr float kCellSize = 4096.0f;

		/// Placed-reference rotation R = Rx(-x) * Ry(-y) * Rz(-z) (column vectors), the convention AnvilLOD's
		/// static LOD uses and that was verified against LODGen/DynDOLOD output. NiMatrix3 is entry[row][col].
		RE::NiMatrix3 ReferenceRotation(float a_x, float a_y, float a_z)
		{
			const float cx = std::cos(-a_x), sx = std::sin(-a_x);
			const float cy = std::cos(-a_y), sy = std::sin(-a_y);
			const float cz = std::cos(-a_z), sz = std::sin(-a_z);

			const float rx[3][3] = { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
			const float ry[3][3] = { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
			const float rz[3][3] = { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } };

			float xy[3][3]{};
			for (int i = 0; i < 3; ++i)
				for (int j = 0; j < 3; ++j)
					for (int k = 0; k < 3; ++k) xy[i][j] += rx[i][k] * ry[k][j];

			RE::NiMatrix3 m;
			for (int i = 0; i < 3; ++i)
				for (int j = 0; j < 3; ++j) {
					float v = 0;
					for (int k = 0; k < 3; ++k) v += xy[i][k] * rz[k][j];
					m.entry[i][j] = v;
				}
			return m;
		}

		std::int32_t CellOf(float a_coord) { return static_cast<std::int32_t>(std::floor(a_coord / kCellSize)); }

		/// The game's own NiObject::Clone (a NiCloningProcess, what the engine uses when a reference loads its 3D).
		/// CreateDeepCopy streams the model through a memory stream instead and crashes on Havok collision
		/// (bhkRigidBody) in full models such as campfires. Declared here because only the 1.7 CommonLib exposes it.
		RE::NiObject* CloneModel(RE::NiObject* a_object)
		{
			using func_t = RE::NiObject* (*)(RE::NiObject*);
			static REL::Relocation<func_t> func{ RELOCATION_ID(68835, 70187) };
			return func(a_object);
		}

		/// Distant copies need no physics: drop collision from the clone (never from the shared template).
		void StripCollision(RE::NiAVObject* a_object, int a_depth = 0)
		{
			if (!a_object || a_depth > 64) {
				return;
			}
			a_object->collisionObject.reset();
			if (auto* node = a_object->AsNode()) {
				for (auto& child : node->GetChildren()) {
					StripCollision(child.get(), a_depth + 1);
				}
			}
		}

		/// Water-shader planes (lakes, pools, streams), as opposed to waterfall / creek effect meshes.
		bool IsWaterMesh(std::string a_path)
		{
			std::ranges::transform(a_path, a_path.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
			return a_path.find("water") != std::string::npos && a_path.find("waterfall") == std::string::npos &&
			       a_path.find("waterwheel") == std::string::npos;
		}

		void CollectControllers(RE::NiObjectNET* a_object, std::vector<RE::NiTimeController*>& a_out)
		{
			if (!a_object) {
				return;
			}
			int guard = 0;
			for (auto* c = a_object->GetControllers(); c && guard < 64; c = c->next.get(), ++guard) {
				if (!c->target) {
					c->SetTarget(a_object);
				}
				a_out.push_back(c);
			}
		}

		/// Every controller in a model: on nodes and geometry (transform / visibility / morph) and on shader
		/// properties (UV scrolling and alpha of waterfalls, creeks, fire). NiControllerManagers go first so their
		/// sequences have driven the interpolators before the controllers that apply them are updated.
		void CollectTree(RE::NiAVObject* a_object, std::vector<RE::NiTimeController*>& a_out, int a_depth = 0)
		{
			if (!a_object || a_depth > 64) {
				return;
			}
			CollectControllers(a_object, a_out);
			if (auto* geom = a_object->AsGeometry()) {
#if ANVILLOD_RUNTIME_LINE == 17 || ANVILLOD_RUNTIME_LINE == 14
				CollectControllers(geom->GetGeometryRuntimeData().shaderProperty.get(), a_out);
#else
				CollectControllers(geom->GetGeometryRuntimeData().properties[RE::BSGeometry::States::kEffect].get(), a_out);
#endif
			}
			if (auto* node = a_object->AsNode()) {
				for (auto& child : node->GetChildren()) {
					CollectTree(child.get(), a_out, a_depth + 1);
				}
			}
		}

		/// Models that animate through a controller manager (windmill blades, water wheels) only move while a
		/// sequence plays; the game starts one when the reference loads, so do the same for our copy.
		void StartSequences(const std::vector<RE::NiTimeController*>& a_controllers)
		{
			for (auto* c : a_controllers) {
				auto* manager = netimmerse_cast<RE::NiControllerManager*>(c);
				if (!manager) {
					continue;
				}
				bool playing = false;
				for (auto& seq : manager->sequenceArray) {
					playing = playing || (seq && seq->Animating());
				}
				if (playing) {
					continue;
				}
				for (auto& seq : manager->sequenceArray) {
					if (seq) {
						seq->Activate(0, false, 1.0f, 0.0f, nullptr, false);
						break;
					}
				}
			}
		}
	}

	Controller& Controller::Get()
	{
		static Controller instance;
		return instance;
	}

	void Controller::OnDataLoaded()
	{
		LoadDynamic();
		LoadUndersides();  // after the dynamic file: they share the mesh string table
		LoadCopies();
	}

	/// AnvilLOD.copies, written by the generator when "Parent > Child copies" is on: the references around Whiterun, Solitude, Windhelm and
	/// Riften that those walled cities' child worldspaces leave out, so the view over the wall is empty. DynDOLOD puts copies of them into
	/// the child worldspace with a plugin; here the model is attached at the reference's place while the player is in that worldspace
	/// and the object is inside the loaded cells (outside them the parent's LOD is drawn).
	void Controller::LoadCopies()
	{
		CopyFile file;
		if (!file.Read("Data/SKSE/Plugins/AnvilLOD/AnvilLOD.copies")) {
			return;
		}
		auto* data = RE::TESDataHandler::GetSingleton();
		if (!data) {
			return;
		}

		std::unordered_map<std::uint32_t, std::uint32_t> meshOf;  // the file's string index -> our shared mesh table
		std::size_t                                      missingWorld = 0;
		for (const auto& e : file.entries) {
			Item item;
			item.world = data->LookupFormID(e.worldLocalId, file.strings[e.worldPlugin]);
			if (item.world == 0) {
				++missingWorld;  // the plugin is not in this load order any more
				continue;
			}
			auto it = meshOf.find(e.model);
			if (it == meshOf.end()) {
				_meshes.push_back(file.strings[e.model]);
				it = meshOf.emplace(e.model, static_cast<std::uint32_t>(_meshes.size() - 1)).first;
			}
			item.mesh = it->second;
			item.pos = RE::NiPoint3(e.pos[0], e.pos[1], e.pos[2]);
			item.rot = ReferenceRotation(e.rot[0], e.rot[1], e.rot[2]);
			item.scale = e.scale;
			item.cellX = CellOf(e.pos[0]);
			item.cellY = CellOf(e.pos[1]);
			item.copy = true;
			item.childCell = (e.flags & CopyEntry::kChildCell) != 0;
			_byWorld[item.world].push_back(_items.size());
			_items.push_back(std::move(item));
			++_copyCount;
		}
		logger::info("Parent > Child copies: {} references around walled cities, {} skipped: plugin not loaded", _copyCount, missingWorld);
	}

	/// AnvilLOD.underside, written by the generator: child worldspaces (Markarth, Sovngarde, Deepwood Redoubt, ...) share cell
	/// coordinates with their parent, so an always-enabled underside reference there is drawn in the parent as a slab.
	/// DynDOLOD enables such references from a script; here the underside is simply drawn while the player is in that worldspace.
	/// One line per worldspace: plugin|local FormID (hex)|model relative to Data\meshes|EditorID.
	void Controller::LoadUndersides()
	{
		std::ifstream in("Data/SKSE/Plugins/AnvilLOD/AnvilLOD.underside");
		auto*         data = RE::TESDataHandler::GetSingleton();
		if (!in || !data) {
			return;
		}

		std::size_t found = 0, missing = 0;
		std::string line;
		while (std::getline(in, line)) {
			if (!line.empty() && line.back() == '\r') {
				line.pop_back();
			}
			if (line.empty() || line[0] == '#') {
				continue;
			}
			std::vector<std::string> parts;
			for (std::size_t start = 0;;) {
				const auto bar = line.find('|', start);
				parts.push_back(line.substr(start, bar == std::string::npos ? bar : bar - start));
				if (bar == std::string::npos) {
					break;
				}
				start = bar + 1;
			}
			if (parts.size() < 3) {
				logger::warn("AnvilLOD.underside: bad line: {}", line);
				continue;
			}
			std::uint32_t localId = 0;
			try {
				localId = static_cast<std::uint32_t>(std::stoul(parts[1], nullptr, 16));
			} catch (const std::exception&) {
				logger::warn("AnvilLOD.underside: bad FormID in line: {}", line);
				continue;
			}
			Item item;
			item.world = data->LookupFormID(localId, parts[0]);
			if (item.world == 0) {
				++missing;  // plugin not in this load order any more
				continue;
			}
			_meshes.push_back(parts[2]);
			item.mesh = static_cast<std::uint32_t>(_meshes.size() - 1);
			item.pos = RE::NiPoint3(0.0f, 0.0f, -500.0f);  // where DynDOLOD and AnvilLOD.esp place it
			item.rot = ReferenceRotation(0.0f, 0.0f, 0.0f);
			item.underside = true;
			_byWorld[item.world].push_back(_items.size());
			_items.push_back(std::move(item));
			++found;
		}
		logger::info("Underside: {} child worldspaces drawn by the plugin, {} skipped: plugin not loaded", found, missing);
	}

	void Controller::LoadDynamic()
	{
		DataFile file;
		if (!file.Read("Data/SKSE/Plugins/AnvilLOD/AnvilLOD.dyn")) {
			return;
		}

		auto* data = RE::TESDataHandler::GetSingleton();
		if (!data) {
			return;
		}

		_meshes = std::move(file.strings);
		std::size_t missingPlugin = 0;
		for (const auto& e : file.entries) {
			Item item;
			item.ref = data->LookupFormID(e.refLocalId, _meshes[e.refPlugin]);
			item.world = data->LookupFormID(e.worldLocalId, _meshes[e.worldPlugin]);
			if (item.ref == 0 || item.world == 0) {
				++missingPlugin;  // plugin not in this load order any more
				continue;
			}
			if (e.parentPlugin != DynamicEntry::kNone) {
				item.parent = data->LookupFormID(e.parentLocalId, _meshes[e.parentPlugin]);
			}
			item.mesh = e.mesh;
			item.pos = RE::NiPoint3(e.pos[0], e.pos[1], e.pos[2]);
			item.rot = ReferenceRotation(e.rot[0], e.rot[1], e.rot[2]);
			item.scale = e.scale;
			item.cellX = CellOf(e.pos[0]);
			item.cellY = CellOf(e.pos[1]);
			item.opposite = (e.flags & DynamicEntry::kOpposite) != 0;
			item.initiallyDisabled = (e.flags & DynamicEntry::kInitiallyDisabled) != 0;
			item.grid = (e.flags & DynamicEntry::kGridObject) != 0;
			item.nearGrid = (e.flags & DynamicEntry::kNearGrid) != 0;
			item.neverFade = (e.flags & DynamicEntry::kNeverFade) != 0;
			item.glow = (e.flags & DynamicEntry::kGlow) != 0;
			item.standIn = (e.flags & DynamicEntry::kStandIn) != 0;
			_glowCount += item.glow ? 1 : 0;
			item.water = item.grid && IsWaterMesh(_meshes[e.mesh]);
			_gridCount += item.grid ? 1 : 0;
			_byWorld[item.world].push_back(_items.size());
			_items.push_back(std::move(item));
		}

		logger::info("Dynamic LOD: {} references in {} worldspaces, {} of them grid objects (water, waterfalls, fires, windmills); {} skipped: plugin not loaded",
			_items.size(), _byWorld.size(), _gridCount, missingPlugin);
	}

	void Controller::Start()
	{
		if (_items.empty() || _running.exchange(true)) {
			return;
		}
		std::thread([this] {
			for (;;) {
				std::this_thread::sleep_for(std::chrono::milliseconds(Settings::Get().updateIntervalMs));
				if (_queued.exchange(true)) {
					continue;  // previous update still waiting for the main thread
				}
				SKSE::GetTaskInterface()->AddTask([this] {
					Update();
					_queued = false;
				});
			}
		}).detach();

		// Animation ticks for drawn grid objects, on the main thread like everything that touches the scene graph.
		if (_gridCount > 0) {
			std::thread([this] {
				for (;;) {
					const auto& s = Settings::Get();
					const auto  fps = s.animate ? s.animationFps : 0u;
					std::this_thread::sleep_for(std::chrono::milliseconds(fps == 0 ? 500 : std::max<std::uint32_t>(8, 1000 / fps)));
					if (fps == 0 || _animatedShown == 0) {
						continue;
					}
					if (_animQueued.exchange(true)) {
						if (Settings::Get().verboseLog && ++_animSkipped % 500 == 1) {
							logger::info("Animate: previous tick still queued ({} skipped so far)", _animSkipped);
						}
						continue;
					}
					SKSE::GetTaskInterface()->AddTask([this] {
						Animate();
						_animQueued = false;
					});
				}
			}).detach();
		}
	}

	void Controller::Reset()
	{
		for (auto i : _shown) {
			_items[i].controllers.clear();
			_items[i].node.reset();
		}
		_shown.clear();
		_animatedShown = 0;
		if (_root && _root->parent) {
			_root->parent->DetachChild(_root.get());
		}
		_root.reset();
	}

	RE::NiNode* Controller::Root()
	{
		auto* tes = RE::TES::GetSingleton();
		if (!tes || !tes->objRoot) {
			return nullptr;
		}
		if (!_root) {
			_root = RE::NiPointer<RE::NiNode>(RE::NiNode::Create());
			_root->name = "AnvilLOD Dynamic LOD";
		}
		if (_root->parent != tes->objRoot) {
			if (_root->parent) {
				_root->parent->DetachChild(_root.get());
			}
			tes->objRoot->AttachChild(_root.get(), true);
		}
		return _root.get();
	}

	std::int32_t Controller::LoadedGridHalf()
	{
		std::int32_t grids = 5;
		if (auto* ini = RE::INISettingCollection::GetSingleton()) {
			if (auto* s = ini->GetSetting("uGridsToLoad:General"); s) {
				grids = static_cast<std::int32_t>(s->data.u);  // data.u exists in both CommonLib lines (GetUInt was renamed in 7.x)
			}
		}
		return std::max(1, grids) / 2;
	}

	bool Controller::IsEnabled(const Item& a_item) const
	{
		// The reference itself, when it's in memory (persistent, or its cell is loaded).
		if (const auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(a_item.ref)) {
			return !ref->IsDisabled() && !ref->IsDeleted();
		}
		// Otherwise follow its enable parent (always persistent, so always in memory).
		if (a_item.parent != 0) {
			if (const auto* parent = RE::TESForm::LookupByID<RE::TESObjectREFR>(a_item.parent)) {
				return parent->IsDisabled() == a_item.opposite;
			}
		}
		return !a_item.initiallyDisabled;
	}

	bool Controller::WantShown(const Item& a_item, const RE::NiPoint3& a_player, std::int32_t a_pcx, std::int32_t a_pcy, std::int32_t a_half, RE::FormID a_playerWorld) const
	{
		const auto& settings = Settings::Get();
		if (a_item.underside) {
			return settings.undersides;  // no reference behind it and no distance: it's there while the player is in its worldspace
		}
		if (a_item.copy) {
			// A reference the child worldspace left out: where the engine has loaded the child's cell it hides the parent's LOD and
			// the object is simply missing, so draw it, but only inside the loaded cells (beyond them the parent's LOD shows it).
			if (!settings.parentCopies || (!settings.parentCopyAllCells && !a_item.childCell)) {
				return false;
			}
			if (a_item.childCell) {
				// The engine hides the parent's LOD in every cell the child worldspace has, loaded or not (measured at Whiterun: the
				// bare area reaches well past the loaded cells), so these are drawn wherever they are; iMaxShown keeps the nearest.
				return true;
			}
			const auto reach = a_half + settings.parentCopyExtraCells;
			return std::abs(a_item.cellX - a_pcx) <= reach && std::abs(a_item.cellY - a_pcy) <= reach;
		}
		if (a_item.grid ?(!settings.gridObjects || (a_item.water && !settings.waterPlanes)) : !settings.enabled) {
			return false;
		}
		if (a_item.glow && (!settings.glowWindows || _glowFactor <= 0.001f)) {
			return false;  // windows are dark by day: nothing to draw
		}
		// Inside the loaded cells the real object is (or would be) there. Not for an object of the parent worldspace seen from a child
		// one (Whiterun's windmills and water planes while the player is in the city's worldspace): the child has no such reference.
		// Except a stand-in the city has the real object for (ChildworldMatches): there the real one is in the loaded cells, and
		// beyond them the stand-in is all there is.
		if ((a_item.world == a_playerWorld || a_item.standIn) && std::abs(a_item.cellX - a_pcx) <= a_half && std::abs(a_item.cellY - a_pcy) <= a_half) {
			return false;
		}
		const float dx = a_item.pos.x - a_player.x, dy = a_item.pos.y - a_player.y;
		const float maxDist = !a_item.grid ? settings.maxDistance :
		                      a_item.neverFade ? std::numeric_limits<float>::max() / 4.0f :
		                      a_item.nearGrid  ? settings.nearGridDistance :
		                                         settings.farGridDistance;
		if (!a_item.neverFade && dx * dx + dy * dy > maxDist * maxDist) {
			return false;
		}
		if (const auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(a_item.ref); ref && ref->Is3DLoaded()) {
			return false;
		}
		return IsEnabled(a_item);
	}

	RE::NiNode* Controller::Template(std::uint32_t a_mesh)
	{
		if (auto it = _templates.find(a_mesh); it != _templates.end()) {
			return it->second.get();
		}
		if (_failedMeshes.contains(a_mesh)) {
			return nullptr;
		}

		RE::BSModelDB::DBTraits::ArgsType args{};
		RE::NiPointer<RE::NiNode>         model;
		const auto&                       path = _meshes[a_mesh];
		auto                              rc = RE::BSModelDB::Demand(path.c_str(), model, args);
		if (rc != RE::BSResource::ErrorCode::kNone || !model) {
			const auto withPrefix = "meshes\\" + path;
			rc = RE::BSModelDB::Demand(withPrefix.c_str(), model, args);
		}
		if (rc != RE::BSResource::ErrorCode::kNone || !model) {
			logger::warn("Dynamic LOD: could not load {}", path);
			_failedMeshes.insert(a_mesh);
			return nullptr;
		}
		auto* raw = model.get();
		_templates.emplace(a_mesh, std::move(model));
		return raw;
	}

	void Controller::Show(Item& a_item)
	{
		auto* root = Root();
		auto* tmpl = root ? Template(a_item.mesh) : nullptr;
		if (!tmpl) {
			return;
		}

		RE::NiPointer<RE::NiObject> copy(CloneModel(tmpl));
		auto* node = copy ? netimmerse_cast<RE::NiAVObject*>(copy.get()) : nullptr;
		if (!node) {
			return;
		}
		StripCollision(node);

		node->local.translate = a_item.pos;
		node->local.rotate = a_item.rot;
		node->local.scale = a_item.scale;
		root->AttachChild(node, true);

		const auto& settings = Settings::Get();
		const bool  animate = a_item.grid && settings.animate && settings.animationFps > 0;
		if (animate) {
			CollectTree(node, a_item.controllers);
			if (!a_item.controllers.empty()) {
				std::ranges::stable_partition(a_item.controllers, [](RE::NiTimeController* c) { return netimmerse_cast<RE::NiControllerManager*>(c) != nullptr; });
				RE::NiTimeController::StartAnimations(node);
				StartSequences(a_item.controllers);
				// A clone's controllers come out never started (start / last time at -FLT_MAX after 100 s of updates, 2026-10-11):
				// do what the loader does to a scene graph that has animations, then start the plain controllers explicitly.
				{
					bool selective = true, rigid = false;
					node->SetSelectiveUpdateFlags(selective, true, rigid);
					const float now = Seconds();
					for (auto* ctl : a_item.controllers) {
						if (!netimmerse_cast<RE::NiControllerManager*>(ctl)) {
							ctl->Start(now);
						}
					}
				}
				++_animatedShown;
				if (settings.verboseLog && _animLogged.size() < 40 && _animLogged.insert(a_item.mesh).second) {
					logger::info("anim {}: {} controllers", _meshes[a_item.mesh], a_item.controllers.size());
					for (auto* ctl : a_item.controllers) {
						const char* name = ctl->GetRTTI() ? ctl->GetRTTI()->GetName() : "?";
						if (auto* mgr = netimmerse_cast<RE::NiControllerManager*>(ctl)) {
							logger::info("  {} sequences={}", name, mgr->sequenceArray.size());
							for (auto& seq : mgr->sequenceArray) {
								if (seq) {
									logger::info("    seq '{}' animating={} state={}", seq->name.c_str(), seq->Animating(), static_cast<int>(seq->state.get()));
								}
							}
						} else {
							logger::info("  {} flags={:#x} freq={} phase={} start={} last={}", name, static_cast<unsigned>(ctl->flags.underlying()), ctl->frequency, ctl->phase, ctl->startTime, ctl->lastTime);
						}
					}
				}
			}
		}

		a_item.node = RE::NiPointer<RE::NiAVObject>(node);
		if (a_item.glow) {
			PrepareGlow(a_item);
		}

		RE::NiUpdateData update{ animate ? Seconds() : 0.0f, RE::NiUpdateData::Flag::kNone };
		node->Update(update);
	}

	namespace
	{
		RE::BSLightingShaderProperty* LightingProperty(RE::BSGeometry* a_geom)
		{
#if ANVILLOD_RUNTIME_LINE == 17 || ANVILLOD_RUNTIME_LINE == 14
			auto* prop = a_geom->GetGeometryRuntimeData().shaderProperty.get();
#else
			auto* prop = a_geom->GetGeometryRuntimeData().properties[RE::BSGeometry::States::kEffect].get();
#endif
			return prop ? netimmerse_cast<RE::BSLightingShaderProperty*>(prop) : nullptr;
		}

		void CollectLightingProperties(RE::NiAVObject* a_object, std::vector<RE::BSLightingShaderProperty*>& a_out, int a_depth = 0)
		{
			if (!a_object || a_depth > 64) {
				return;
			}
			if (auto* geom = a_object->AsGeometry()) {
				if (auto* prop = LightingProperty(geom)) {
					a_out.push_back(prop);
				}
			}
			if (auto* node = a_object->AsNode()) {
				for (auto& child : node->GetChildren()) {
					CollectLightingProperties(child.get(), a_out, a_depth + 1);
				}
			}
		}

		/// 0 by day, 1 at night: fades in from dusk start to dusk end, stays on past midnight, fades out from dawn start to dawn end.
		float GlowAmount(float a_hour, const Settings& a_s)
		{
			const auto ramp = [](float x, float a, float b) { return b <= a ? (x >= b ? 1.0f : 0.0f) : std::clamp((x - a) / (b - a), 0.0f, 1.0f); };
			if (a_hour >= a_s.glowDuskStart) {
				return ramp(a_hour, a_s.glowDuskStart, a_s.glowDuskEnd);
			}
			if (a_hour <= a_s.glowDawnEnd) {
				return 1.0f - ramp(a_hour, a_s.glowDawnStart, a_s.glowDawnEnd);
			}
			return 0.0f;
		}
	}

	/// The glow meshes carry the External_Emittance shader flag: the engine then takes their glow from the reference's emittance region, which
	/// a model we attach has none of, so it would stay dark. Switch it to the mesh's own emissive and drive the multiple by the hour ourselves.
	void Controller::PrepareGlow(Item& a_item)
	{
		a_item.glowProps.clear();
		std::vector<RE::BSLightingShaderProperty*> props;
		CollectLightingProperties(a_item.node.get(), props);
		// The mesh's own emissive multiples, read once: a clone may share its shader properties with the template, and ours scale them.
		auto& bases = _glowBases[a_item.mesh];
		if (bases.size() != props.size()) {
			bases.clear();
			for (auto* prop : props) {
				bases.push_back(prop->emissiveMult > 0.0f ? prop->emissiveMult : 3.0f);
			}
		}
		for (std::size_t i = 0; i < props.size(); ++i) {
			props[i]->SetFlags(RE::BSShaderProperty::EShaderPropertyFlag8::kExternalEmittance, false);
			props[i]->SetFlags(RE::BSShaderProperty::EShaderPropertyFlag8::kOwnEmit, true);
			a_item.glowProps.emplace_back(props[i], bases[i]);
		}
		const float scale = _glowFactor * Settings::Get().glowIntensity;
		for (auto& [prop, base] : a_item.glowProps) {
			prop->emissiveMult = base * scale;
		}
	}

	void Controller::ApplyGlow()
	{
		const float scale = _glowFactor * Settings::Get().glowIntensity;
		for (auto i : _shown) {
			auto& item = _items[i];
			if (!item.glow || !item.node) {
				continue;
			}
			for (auto& [prop, base] : item.glowProps) {
				prop->emissiveMult = base * scale;
			}
		}
	}

	float Controller::Seconds() const
	{
		return std::chrono::duration<float>(std::chrono::steady_clock::now() - _epoch).count();
	}

	void Controller::Animate()
	{
		const float t = Seconds();
		RE::NiUpdateData update{ t, RE::NiUpdateData::Flag::kNone };
		++_animTick;
		if (Settings::Get().verboseLog && (_animTick == 1 || _animTick % 500 == 0)) {
			logger::info("Animate tick {}: {} drawn, {} animated, t={:.2f}", _animTick, _shown.size(), _animatedShown.load(), t);
		}
		const bool probe = Settings::Get().verboseLog && (_animTick % 150 == 3);
		std::unordered_set<std::uint32_t> probed;
		for (auto i : _shown) {
			auto& item = _items[i];
			if (item.controllers.empty() || !item.node) {
				continue;
			}
			// The scene graph's own update pass; it left every controller of a clone untouched (start / last time never set,
			// 2026-10-11 log), so a controller manager (windmill blades, water wheels: they animate through sequences) is also
			// ticked itself. Plain controllers are not: calling those directly crashed in
			// BSLightingShaderPropertyFloatController::Update on cloned models (crash 2026-10-08).
			item.node->Update(update);
			if (Settings::Get().tickManagers) {
				for (auto* ctl : item.controllers) {
					if (auto* mgr = netimmerse_cast<RE::NiControllerManager*>(ctl)) {
						mgr->Update(t);
					}
				}
			}
			if (probe && probed.size() < 12 && probed.insert(item.mesh).second) {
				logger::info("animprobe tick {} t={:.2f} {}", _animTick, t, _meshes[item.mesh]);
				for (auto* ctl : item.controllers) {
					logger::info("  {} flags={:#x} target={} last={:.2f} start={:.2f} scaled={:.2f}", ctl->GetRTTI() ? ctl->GetRTTI()->GetName() : "?", static_cast<unsigned>(ctl->flags.underlying()), ctl->target ? "set" : "NULL", ctl->lastTime, ctl->startTime, ctl->scaledTime);
				}
			}
		}
	}

	void Controller::HideAll()
	{
		for (auto i : _shown) {
			Hide(_items[i]);
		}
		_shown.clear();
	}

	void Controller::Hide(Item& a_item)
	{
		if (!a_item.controllers.empty()) {
			a_item.controllers.clear();
			if (_animatedShown > 0) {
				--_animatedShown;
			}
		}
		a_item.glowProps.clear();
		if (a_item.node) {
			if (a_item.node->parent) {
				a_item.node->parent->DetachChild(a_item.node.get());
			}
			a_item.node.reset();
		}
	}

	void Controller::Update()
	{
		struct CountCopies
		{
			Controller& c;
			~CountCopies()
			{
				std::size_t n = 0;
				for (auto i : c._shown) {
					n += c._items[i].copy ? 1 : 0;
				}
				c._copiesShown = n;
				if (Settings::Get().verboseLog && ++c._updateCount % 10 == 0) {
					std::size_t kids = 0;
					if (c._root) {
						for (auto& child : c._root->GetChildren()) {
							kids += child ? 1 : 0;
						}
					}
					logger::info("Update #{}: {} drawn ({} copies), root {} with {} children, flags {:#x}, parent {}", c._updateCount, c._shown.size(), n,
						c._root ? "present" : "ABSENT", kids, c._root ? static_cast<unsigned>(c._root->GetFlags().underlying()) : 0u,
						c._root && c._root->parent ? c._root->parent->name.c_str() : "none");
				}
			}
		} countCopies{ *this };
		const auto& settings = Settings::Get();
		if (_refresh.exchange(false)) {
			// Hide now, draw again on the next update: detaching and re-attaching in the same pass left the objects around the player
			// invisible until the next cell change (Redraw now, 2026-10-11).
			HideAll();
			// ... and with a new root too, like after a cell change (entering and leaving a building brought the objects back, a plain
			// hide and draw did not).
			if (_root && _root->parent) {
				_root->parent->DetachChild(_root.get());
			}
			_root.reset();
			_holdUntil = std::chrono::steady_clock::now() + std::chrono::seconds(3);
			return;
		}
		if (std::chrono::steady_clock::now() < _holdUntil) {
			return;
		}
		auto*       player = RE::PlayerCharacter::GetSingleton();
		auto*       world = player ? player->GetWorldspace() : nullptr;
		if (_glowCount > 0) {
			if (auto* calendar = RE::Calendar::GetSingleton()) {
				_glowFactor = GlowAmount(calendar->GetHour(), settings);
			}
		}

		// Which entries can be visible: the current worldspace, plus its parent's when it uses the parent's LOD
		// (Whiterun's city space shows Tamriel's LOD, so Tamriel's dynamic LOD belongs there too).
		std::vector<const std::vector<std::size_t>*> lists;
		if ((settings.enabled || settings.gridObjects || settings.undersides || settings.parentCopies) && world) {
			if (auto it = _byWorld.find(world->GetFormID()); it != _byWorld.end()) {
				lists.push_back(&it->second);
			}
			if (world->parentWorld && world->parentUseFlags.all(RE::TESWorldSpace::ParentUseFlag::kUseLODData)) {
				if (auto it = _byWorld.find(world->parentWorld->GetFormID()); it != _byWorld.end()) {
					lists.push_back(&it->second);
				}
			}
		}

		// Hide what shouldn't be shown any more (or everything, in interiors / other worldspaces).
		const auto pos = player ? player->GetPosition() : RE::NiPoint3{};
		const auto pcx = CellOf(pos.x), pcy = CellOf(pos.y);
		const auto half = LoadedGridHalf();
		const RE::FormID playerWorld = world ? world->GetFormID() : 0;
		std::unordered_set<RE::FormID> activeWorlds;
		for (auto* list : lists) {
			if (!list->empty()) {
				activeWorlds.insert(_items[list->front()].world);
			}
		}

		std::vector<std::size_t> stillShown;
		stillShown.reserve(_shown.size());
		for (auto i : _shown) {
			auto& item = _items[i];
			if (activeWorlds.contains(item.world) && WantShown(item, pos, pcx, pcy, half, playerWorld)) {
				stillShown.push_back(i);
			} else {
				Hide(item);
				if (settings.verboseLog) {
					logger::info("hide {:08X}", item.ref);
				}
			}
		}
		_shown = std::move(stillShown);
		_playerCellX = pcx;
		_playerCellY = pcy;
		if (_glowCount > 0) {
			ApplyGlow();
		}

		// Show newly wanted ones, nearest first: when the cap is reached it is the farthest that are left out, never the ones next to the
		// player (the list is in FormID order, so cutting it off in order dropped whole areas, the nearby water planes among them).
		std::vector<std::pair<float, std::size_t>> wanted;
		for (auto* list : lists) {
			for (auto i : *list) {
				auto& item = _items[i];
				if (item.node || !WantShown(item, pos, pcx, pcy, half, playerWorld)) {
					continue;
				}
				const float dx = item.pos.x - pos.x, dy = item.pos.y - pos.y;
				wanted.emplace_back(dx * dx + dy * dy, i);
			}
		}
		std::ranges::sort(wanted);
		for (const auto& [distance, i] : wanted) {
			auto& item = _items[i];
			if (_shown.size() >= settings.maxShown) {
				if (!_capWarned) {
					logger::warn("Dynamic LOD: reached iMaxShown ({}), the {} farthest objects wanted are not drawn; raise it in the menu or AnvilLOD.ini", settings.maxShown, wanted.size());
					_capWarned = true;
				}
				return;
			}
			Show(item);
			if (item.node) {
				_shown.push_back(i);
				if (settings.verboseLog) {
					logger::info("show {:08X} {}", item.ref, _meshes[item.mesh]);
				}
			}
		}
	}
}
