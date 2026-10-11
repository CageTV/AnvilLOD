#pragma once

#include "DataFile.h"

namespace AnvilLOD
{
	/// Dynamic LOD: for references whose enable state changes while playing (quest-built or -destroyed places),
	/// AnvilLOD leaves them out of the static LOD blocks and this controller draws their LOD mesh instead, only
	/// while the reference is enabled, outside the loaded cells and within range.
	///
	/// Grid objects (DynDOLOD's Grid column: water planes, waterfalls, creeks, fires, windmills, ships) have no
	/// static LOD at all; they're drawn the same way, with their controllers ticked so they stay animated.
	class Controller
	{
	public:
		static Controller& Get();

		/// kDataLoaded: read AnvilLOD.dyn and resolve plugin + local FormIDs against the current load order.
		void OnDataLoaded();
		/// Game loaded / new game: drop everything we attached, it's rebuilt on the next update.
		void Reset();
		/// Starts the background timer that queues Update() on the main thread.
		void Start();

		std::size_t ItemCount() const { return _items.size(); }
		std::size_t ShownCount() const { return _shown.size(); }
		std::size_t WorldCount() const { return _byWorld.size(); }
		std::size_t GridCount() const { return _gridCount; }
		std::size_t CopyCount() const { return _copyCount; }
		std::size_t GlowCount() const { return _glowCount; }
		std::size_t CopiesShown() const { return _copiesShown; }
		std::int32_t PlayerCellX() const { return _playerCellX; }
		std::int32_t PlayerCellY() const { return _playerCellY; }
		float       GlowFactor() const { return _glowFactor; }
		std::size_t AnimatedCount() const { return _animatedShown; }
		/// Drops every drawn object so the next update redraws with current settings.
		void Refresh() { _refresh = true; }

	private:
		struct Item
		{
			RE::FormID                     ref{ 0 };
			RE::FormID                     parent{ 0 };
			RE::FormID                     world{ 0 };
			std::uint32_t                  mesh{ 0 };
			RE::NiPoint3                   pos;
			RE::NiMatrix3                  rot;
			float                          scale{ 1.0f };
			std::int32_t                   cellX{ 0 };
			std::int32_t                   cellY{ 0 };
			bool                           opposite{ false };
			bool                           initiallyDisabled{ false };
			bool                           grid{ false };
			bool                           nearGrid{ false };
			bool                           neverFade{ false };
			bool                           water{ false };
			bool                           underside{ false };  // a child worldspace's terrain underside: drawn whenever the player is in `world`
			bool                           copy{ false };       // a Parent > Child copy (AnvilLOD.copies): drawn while the player is in `world`, inside the loaded cells
			bool                           childCell{ false };  // ... and the child worldspace has a cell here
			bool                           glow{ false };       // a window glow mesh: its emissive multiple follows the hour
			bool                           standIn{ false };    // a parent-worldspace stand-in for a real object of the city worldspace
			std::vector<std::pair<RE::BSLightingShaderProperty*, float>> glowProps;  // shader properties of the drawn clone and their own emissive multiple
			RE::NiPointer<RE::NiAVObject>  node;
			std::vector<RE::NiTimeController*> controllers;  // owned by node's tree; valid while node is held
		};

		void LoadDynamic();
		void LoadUndersides();
		void LoadCopies();
		void PrepareGlow(Item& a_item);
		void ApplyGlow();
		void Update();
		void Animate();
		float Seconds() const;
		void HideAll();
		bool WantShown(const Item& a_item, const RE::NiPoint3& a_player, std::int32_t a_pcx, std::int32_t a_pcy, std::int32_t a_half, RE::FormID a_playerWorld) const;
		bool IsEnabled(const Item& a_item) const;
		void Show(Item& a_item);
		void Hide(Item& a_item);
		RE::NiNode* Root();
		RE::NiNode* Template(std::uint32_t a_mesh);
		static std::int32_t LoadedGridHalf();

		std::vector<std::string>                                    _meshes;
		std::vector<Item>                                           _items;
		std::unordered_map<RE::FormID, std::vector<std::size_t>>    _byWorld;
		std::unordered_map<std::uint32_t, RE::NiPointer<RE::NiNode>> _templates;
		std::unordered_set<std::uint32_t>                           _failedMeshes;
		RE::NiPointer<RE::NiNode>                                   _root;
		std::vector<std::size_t>                                    _shown;
		std::atomic<bool>                                           _queued{ false };
		std::atomic<bool>                                           _running{ false };
		bool                                                        _capWarned{ false };
		std::atomic<bool>                                           _animQueued{ false };
		std::atomic<bool>                                           _refresh{ false };
		std::size_t                                                 _gridCount{ 0 };
		std::size_t                                                 _copyCount{ 0 };
		std::size_t                                                 _glowCount{ 0 };
		std::atomic<std::size_t>                                    _copiesShown{ 0 };
		std::atomic<std::int32_t>                                   _playerCellX{ 0 };
		std::atomic<std::int32_t>                                   _playerCellY{ 0 };
		float                                                       _glowFactor{ 0.0f };  // 0 by day, 1 at night, from the game hour
		std::unordered_map<std::uint32_t, std::vector<float>>       _glowBases;           // per mesh: the emissive multiple of each of its shaders, as the file has it
		std::atomic<std::size_t>                                    _animatedShown{ 0 };
		std::chrono::steady_clock::time_point                       _epoch{ std::chrono::steady_clock::now() };
	};
}
