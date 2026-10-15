#pragma once

namespace AnvilLOD
{
	/// Settings from Data\SKSE\Plugins\AnvilLOD.ini (all optional).
	struct Settings
	{
		bool          enabled{ true };
		float         maxDistance{ 120000.0f };  // dynamic LOD is drawn up to this far from the player (game units)
		std::uint32_t updateIntervalMs{ 500 };   // how often enable states and distances are checked
		std::uint32_t maxShown{ 8000 };          // safety cap on dynamic LOD objects in the scene
		bool          verboseLog{ false };

		// Grid objects: water planes, waterfalls, fires, windmills (DynDOLOD's Grid column), always drawn beyond the loaded cells
		bool          gridObjects{ true };
		bool          waterPlanes{ true };          // includes meshes with the water shader (lakes, pools, streams)
		float         nearGridDistance{ 40000.0f }; // "Near LOD" objects
		float         farGridDistance{ 120000.0f }; // "Far LOD" / "Far Full" objects
		float         effectsDistance{ 20000.0f };  // fires, candles and streams of AnvilLOD Effects.esp are only enabled this close to the player
		bool          driveSequences{ true };       // play the transform sequences of drawn clones ourselves (windmill blades, water wheels)
		bool          tickManagers{ false };        // also call the engine's NiControllerManager::Update on drawn clones: OFF, it crashed on the 1.6.1170 build at the first tick (their sequence times are never initialised, 2026-10-15). Key is bTickEngineManagers so an old bTickManagers=1 in a user's ini is ignored
		bool          animate{ false };             // EXPERIMENTAL: keep grid objects animated (bAnimateExperimental)
		std::uint32_t animationFps{ 30 };           // update ticks per second while animate is on
		bool          animateWater{ true };         // scroll the texture of the water copies around the walled cities (the engine never animates a model a plugin attaches)
		float         waterScrollSpeed{ 0.012f };   // water texture tiles per second
		float         waterOpacity{ 1.0f };         // multiplies the opacity of the water copies around the walled cities (live, from the menu)

		// Terrain underside of child worldspaces (AnvilLOD.underside), drawn only while the player is in that worldspace
		bool          undersides{ true };
		bool          cityGrass{ true };            // real grass in the walled cities' child worldspaces (clears their NoGrass flag when the game loads; needs a restart)

		// Parent > Child copies (AnvilLOD.copies): the objects around Whiterun, Solitude, Windhelm and Riften that their child
		// worldspaces leave out, drawn while the player is in the city and the object is inside the loaded cells
		// Window glow (DynDOLOD's <model>glow_lod_N.nif meshes), drawn beyond the loaded cells and lit by the hour
		bool          glowWindows{ true };
		float         glowIntensity{ 1.0f };   // multiplies the mesh's own emissive multiple at full night
		float         glowDuskStart{ 18.0f };  // the glow fades in from here ...
		float         glowDuskEnd{ 20.5f };    // ... to full here
		float         glowDawnStart{ 4.5f };   // and fades out from here ...
		float         glowDawnEnd{ 6.5f };     // ... to nothing here

		// Adaptive quality (Adaptive.cpp): steps the quality layers down while the frame rate stays under the target
		bool          adaptiveQuality{ false };
		std::int32_t  adaptiveTargetFps{ 40 };
		std::int32_t  adaptiveFloor{ 1 };            // the lowest preset it may step down to: 0 Max, 1 High (keeps all the grass), 2 Medium, 3 Low

		bool          parentCopies{ true };
		std::int32_t  parentCopyExtraCells{ 0 };    // draw copies this many cells beyond the loaded ones too (to find how far the engine hides the parent's LOD)
		bool          parentCopyAllCells{ false };  // false: only in cells the child worldspace has (where the engine hides the parent's LOD)

		static Settings& Get();
		void             Load();
		bool             Save() const;  // writes the [Dynamic] values back to AnvilLOD.ini
	};
}
