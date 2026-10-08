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

		static Settings& Get();
		void             Load();
		bool             Save() const;  // writes the [Dynamic] values back to AnvilLOD.ini
	};
}
