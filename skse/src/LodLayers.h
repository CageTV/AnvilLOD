#pragma once

namespace AnvilLOD
{
	/// Quality layers of the object LOD blocks. The generator writes grass, 3D trees and tree cards into their own
	/// shapes under a node named "AL:<layer>:<LOD level>" (see src/AnvilLOD.Core/Lod/LodLayers.cs). This finds those
	/// nodes in the loaded scene and hides or shows them, so the quality can be changed in game without regenerating.
	///
	/// Hiding a layer saves draw cost (triangles, overdraw). The data stays loaded, so memory and load time are set
	/// by what was generated.
	class Layers
	{
	public:
		enum Layer : int { kGrass, kTree3D, kTreeCard, kLayerCount };
		static constexpr int kLevelCount = 4;  // LOD4, LOD8, LOD16, LOD32

		static Layers& Get();

		Layers()
		{
			for (auto& row : show) {
				for (auto& v : row) v = true;
			}
		}

		static const char* LayerName(int a_layer);   // "Grass", "3D trees", "Tree cards"
		static const char* LayerKey(int a_layer);    // "grass", "tree3d", "treecard" (also the node name part)
		static int         LevelNumber(int a_level); // 4, 8, 16, 32

		/// Reads bShow_<layer>_<level> from AnvilLOD.ini (everything shown by default).
		void Load();
		/// Writes them to the [Layers] section.
		bool Save() const;
		/// Starts the background timer that queues Apply() on the main thread.
		void Start();
		/// A setting changed: re-apply on the next tick.
		void Changed() { _dirty = true; }
		/// 0 = Max (everything), 1 = High, 2 = Medium, 3 = Low.
		void Preset(int a_preset);

		/// How many nodes of each layer and level were found in the loaded scene at the last scan (0 = not in the LOD files).
		int  Found(int a_layer, int a_level) const { return _found[a_layer][a_level]; }
		bool AnyFound() const { return _anyFound; }

		bool show[kLayerCount][kLevelCount]{};

	private:
		void Apply();
		bool AnyHidden() const;

		std::atomic<bool> _dirty{ true };
		std::atomic<bool> _queued{ false };
		std::atomic<bool> _running{ false };
		bool              _appliedHidden{ false };
		bool              _anyFound{ false };
		bool              _logged{ false };
		int               _scans{ 0 };
		RE::NiPointer<RE::NiNode> _lodRoot;  // the engine's "LODRoot" node (found in game: WorldRoot < shadow scene node < LODRoot < LandLOD < ... < obj)
		bool              _objLogged{ false };
		int               _found[kLayerCount][kLevelCount]{};
	};
}
