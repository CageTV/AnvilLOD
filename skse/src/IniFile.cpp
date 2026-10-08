#include "IniFile.h"

namespace AnvilLOD::IniFile
{
	namespace
	{
		std::string Lower(std::string s)
		{
			for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			return s;
		}

		std::string Trim(const std::string& s)
		{
			const auto a = s.find_first_not_of(" \t\r\n");
			if (a == std::string::npos) return {};
			const auto b = s.find_last_not_of(" \t\r\n");
			return s.substr(a, b - a + 1);
		}
	}

	bool Write(const std::filesystem::path& a_path, std::string_view a_section, const std::vector<std::pair<std::string, std::string>>& a_values)
	{
		std::vector<std::string> lines;
		{
			std::ifstream in(a_path);
			std::string line;
			while (std::getline(in, line)) {
				if (!line.empty() && line.back() == '\r') line.pop_back();
				lines.push_back(line);
			}
		}

		const auto want = Lower(std::string(a_section));
		std::vector<bool> done(a_values.size(), false);
		std::ptrdiff_t sectionEnd = -1;  // index to insert missing keys before
		bool inSection = false;

		for (std::size_t i = 0; i < lines.size(); ++i) {
			const auto t = Trim(lines[i]);
			if (!t.empty() && t.front() == '[') {
				if (inSection) sectionEnd = static_cast<std::ptrdiff_t>(i);
				inSection = Lower(Trim(t.substr(1, t.find(']') - 1))) == want;
				if (inSection) sectionEnd = static_cast<std::ptrdiff_t>(lines.size());
				continue;
			}
			if (!inSection || t.empty() || t[0] == ';' || t[0] == '#') continue;
			const auto eq = t.find('=');
			if (eq == std::string::npos) continue;
			const auto key = Lower(Trim(t.substr(0, eq)));
			for (std::size_t k = 0; k < a_values.size(); ++k) {
				if (Lower(a_values[k].first) == key) {
					lines[i] = a_values[k].first + "=" + a_values[k].second;
					done[k] = true;
				}
			}
		}

		std::vector<std::string> missing;
		for (std::size_t k = 0; k < a_values.size(); ++k)
			if (!done[k]) missing.push_back(a_values[k].first + "=" + a_values[k].second);

		if (!missing.empty()) {
			if (sectionEnd < 0) {
				if (!lines.empty() && !Trim(lines.back()).empty()) lines.emplace_back();
				lines.push_back("[" + std::string(a_section) + "]");
				lines.insert(lines.end(), missing.begin(), missing.end());
			} else {
				// Insert before trailing blank lines of the section.
				auto at = sectionEnd;
				while (at > 0 && Trim(lines[static_cast<std::size_t>(at - 1)]).empty()) --at;
				lines.insert(lines.begin() + at, missing.begin(), missing.end());
			}
		}

		std::error_code ec;
		std::filesystem::create_directories(a_path.parent_path(), ec);
		std::ofstream out(a_path, std::ios::trunc | std::ios::binary);
		if (!out) return false;
		for (const auto& l : lines) out << l << "\r\n";
		return static_cast<bool>(out);
	}
}
