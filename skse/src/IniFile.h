#pragma once

namespace AnvilLOD::IniFile
{
	/// Sets key=value pairs in one [section] of an ini file, keeping every other line (comments included).
	/// Keys are matched case-insensitively; missing keys are added at the end of the section, a missing section
	/// at the end of the file. Returns false if the file couldn't be written.
	bool Write(const std::filesystem::path& a_path, std::string_view a_section, const std::vector<std::pair<std::string, std::string>>& a_values);
}
