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
	}

	Controller& Controller::Get()
	{
		static Controller instance;
		return instance;
	}

	void Controller::OnDataLoaded()
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
			_byWorld[item.world].push_back(_items.size());
			_items.push_back(std::move(item));
		}

		logger::info("Dynamic LOD: {} references in {} worldspaces ({} skipped: plugin not loaded)", _items.size(), _byWorld.size(), missingPlugin);
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
	}

	void Controller::Reset()
	{
		for (auto i : _shown) {
			_items[i].node.reset();
		}
		_shown.clear();
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

	bool Controller::WantShown(const Item& a_item, const RE::NiPoint3& a_player, std::int32_t a_pcx, std::int32_t a_pcy, std::int32_t a_half) const
	{
		// Inside the loaded cells the real object is (or would be) there.
		if (std::abs(a_item.cellX - a_pcx) <= a_half && std::abs(a_item.cellY - a_pcy) <= a_half) {
			return false;
		}
		const float dx = a_item.pos.x - a_player.x, dy = a_item.pos.y - a_player.y;
		const float maxDist = Settings::Get().maxDistance;
		if (dx * dx + dy * dy > maxDist * maxDist) {
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

		RE::NiPointer<RE::NiObject> copy;
		tmpl->CreateDeepCopy(copy);
		auto* node = copy ? netimmerse_cast<RE::NiAVObject*>(copy.get()) : nullptr;
		if (!node) {
			return;
		}

		node->local.translate = a_item.pos;
		node->local.rotate = a_item.rot;
		node->local.scale = a_item.scale;
		root->AttachChild(node, true);

		RE::NiUpdateData update{ 0.0f, RE::NiUpdateData::Flag::kNone };
		node->Update(update);
		a_item.node = RE::NiPointer<RE::NiAVObject>(node);
	}

	void Controller::Hide(Item& a_item)
	{
		if (a_item.node) {
			if (a_item.node->parent) {
				a_item.node->parent->DetachChild(a_item.node.get());
			}
			a_item.node.reset();
		}
	}

	void Controller::Update()
	{
		const auto& settings = Settings::Get();
		auto*       player = RE::PlayerCharacter::GetSingleton();
		auto*       world = player ? player->GetWorldspace() : nullptr;

		// Which entries can be visible: the current worldspace, plus its parent's when it uses the parent's LOD
		// (Whiterun's city space shows Tamriel's LOD, so Tamriel's dynamic LOD belongs there too).
		std::vector<const std::vector<std::size_t>*> lists;
		if (settings.enabled && world) {
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
			if (activeWorlds.contains(item.world) && WantShown(item, pos, pcx, pcy, half)) {
				stillShown.push_back(i);
			} else {
				Hide(item);
				if (settings.verboseLog) {
					logger::info("hide {:08X}", item.ref);
				}
			}
		}
		_shown = std::move(stillShown);

		// Show newly wanted ones.
		for (auto* list : lists) {
			for (auto i : *list) {
				auto& item = _items[i];
				if (item.node || !WantShown(item, pos, pcx, pcy, half)) {
					continue;
				}
				if (_shown.size() >= settings.maxShown) {
					if (!_capWarned) {
						logger::warn("Dynamic LOD: reached iMaxShown ({}), more objects are not drawn", settings.maxShown);
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
}
