#pragma once

namespace AnvilLOD
{
	/// One switchable reference from AnvilLOD.dyn, as written by the AnvilLOD generator.
	struct DynamicEntry
	{
		std::uint32_t refPlugin;
		std::uint32_t refLocalId;
		std::uint32_t worldPlugin;
		std::uint32_t worldLocalId;
		std::uint32_t mesh;
		std::array<float, 3> pos;
		std::array<float, 3> rot;  // radians, as stored in the REFR
		float                scale;
		std::uint32_t        parentPlugin;  // 0xFFFFFFFF = no enable parent
		std::uint32_t        parentLocalId;
		std::uint32_t        flags;  // see k* below

		static constexpr std::uint32_t kNone = 0xFFFFFFFF;
		static constexpr std::uint32_t kOpposite = 1;
		static constexpr std::uint32_t kInitiallyDisabled = 2;
		static constexpr std::uint32_t kGridObject = 4;   // water / waterfall / fire / windmill: always dynamic, animated
		static constexpr std::uint32_t kNearGrid = 8;     // "Near LOD": drawn only near the loaded cells
		static constexpr std::uint32_t kNeverFade = 16;   // "Never Fade LOD": drawn at any distance
		static constexpr std::uint32_t kGlow = 32;        // window glow mesh: lit by the hour (night only), not always on
		static constexpr std::uint32_t kNativeBacked = 128;  // a real reference in AnvilLOD.esp draws it in its own worldspace: the plugin only draws the model in other worldspaces (a walled city)
		static constexpr std::uint32_t kStandIn = 64;     // Tamriel's stand-in for a real object of a city worldspace: hidden inside the loaded cells while the player is in that city
	};

	/// One Parent > Child copy from AnvilLOD.copies: a parent worldspace reference around a walled city, drawn while the player is
	/// in the city's child worldspace (no plugin, no record).
	struct CopyEntry
	{
		std::uint32_t        worldPlugin;
		std::uint32_t        worldLocalId;
		std::uint32_t        model;  // string index, relative to Data\meshes
		std::array<float, 3> pos;
		std::array<float, 3> rot;  // radians, as stored in the REFR
		float                scale;
		std::uint32_t        flags;

		static constexpr std::uint32_t kChildCell = 1;     // the child worldspace has a cell at these coordinates
		static constexpr std::uint32_t kAltTextures = 2;   // the base record swaps textures; the copy shows the model's own
	};

	struct CopyFile
	{
		std::vector<std::string> strings;
		std::vector<CopyEntry>   entries;

		/// Reads Data\SKSE\Plugins\AnvilLOD\AnvilLOD.copies. Returns false (quietly when it just isn't there) if it can't be used.
		bool Read(const std::filesystem::path& a_path);
	};

	/// One grid object that is a real reference in AnvilLOD.esp (AnvilLOD.native): the vanilla reference it stands for, its enable
	/// parent, and the real reference whose enable state follows the source's.
	struct NativeEntry
	{
		std::uint32_t sourcePlugin;
		std::uint32_t sourceLocalId;
		std::uint32_t nativePlugin;
		std::uint32_t nativeLocalId;
		std::uint32_t parentPlugin;  // 0xFFFFFFFF = no enable parent
		std::uint32_t parentLocalId;
		std::uint32_t flags;
		float         x{ 0 };  // version 2: where the reference is (for kNear)
		float         y{ 0 };
		float         z{ 0 };

		static constexpr std::uint32_t kNone = 0xFFFFFFFF;
		static constexpr std::uint32_t kOpposite = 1;
		static constexpr std::uint32_t kInitiallyDisabled = 2;
		static constexpr std::uint32_t kNear = 4;  // only enabled within the near grid distance of the player (fires, candles, streams)
	};

	struct NativeFile
	{
		std::vector<std::string> strings;
		std::vector<NativeEntry> entries;

		/// Reads Data\SKSE\Plugins\AnvilLOD\AnvilLOD.native. Returns false (quietly when it just isn't there) if it can't be used.
		bool Read(const std::filesystem::path& a_path);
	};

	struct DataFile
	{
		std::vector<std::string>  strings;
		std::vector<DynamicEntry> entries;

		/// Reads Data\SKSE\Plugins\AnvilLOD\AnvilLOD.dyn. Returns false (and logs why) if it's missing or broken.
		bool Read(const std::filesystem::path& a_path);
	};
}
