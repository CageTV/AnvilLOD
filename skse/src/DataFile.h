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
		std::uint32_t        flags;  // 1 = opposite of parent, 2 = initially disabled

		static constexpr std::uint32_t kNone = 0xFFFFFFFF;
		static constexpr std::uint32_t kOpposite = 1;
		static constexpr std::uint32_t kInitiallyDisabled = 2;
	};

	struct DataFile
	{
		std::vector<std::string>  strings;
		std::vector<DynamicEntry> entries;

		/// Reads Data\SKSE\Plugins\AnvilLOD\AnvilLOD.dyn. Returns false (and logs why) if it's missing or broken.
		bool Read(const std::filesystem::path& a_path);
	};
}
