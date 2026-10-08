#pragma once

namespace AnvilLOD
{
	/// Settings from Data\SKSE\Plugins\AnvilLOD.ini (all optional).
	struct Settings
	{
		bool          enabled{ true };
		float         maxDistance{ 120000.0f };  // dynamic LOD is drawn up to this far from the player (game units)
		std::uint32_t updateIntervalMs{ 500 };   // how often enable states and distances are checked
		std::uint32_t maxShown{ 4000 };          // safety cap on dynamic LOD objects in the scene
		bool          verboseLog{ false };

		// Grid objects: water planes, waterfalls, fires, windmills (DynDOLOD's Grid column), always drawn beyond the loaded cells
		bool          gridObjects{ true };
		bool          waterPlanes{ true };          // includes meshes with the water shader (lakes, pools, streams)
		float         nearGridDistance{ 40000.0f }; // "Near LOD" objects
		float         farGridDistance{ 120000.0f }; // "Far LOD" / "Far Full" objects
		bool          animate{ false };             // EXPERIMENTAL: keep grid objects animated (bAnimateExperimental)
		std::uint32_t animationFps{ 30 };           // update ticks per second while animate is on

		static Settings& Get();
		void             Load();
		bool             Save() const;  // writes the [Dynamic] values back to AnvilLOD.ini
	};
}
