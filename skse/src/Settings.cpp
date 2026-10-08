#include "Settings.h"
#include "IniFile.h"

namespace AnvilLOD
{
	Settings& Settings::Get()
	{
		static Settings instance;
		return instance;
	}

	namespace
	{
		std::string Trim(std::string s)
		{
			const auto first = s.find_first_not_of(" \t\r\n");
			if (first == std::string::npos) {
				return {};
			}
			const auto last = s.find_last_not_of(" \t\r\n");
			return s.substr(first, last - first + 1);
		}

		bool EqualsNoCase(std::string_view a, std::string_view b)
		{
			if (a.size() != b.size()) {
				return false;
			}
			for (std::size_t i = 0; i < a.size(); ++i) {
				if (std::tolower(static_cast<unsigned char>(a[i])) != std::tolower(static_cast<unsigned char>(b[i]))) {
					return false;
				}
			}
			return true;
		}
	}

	void Settings::Load()
	{
		const std::filesystem::path path{ "Data/SKSE/Plugins/AnvilLOD.ini" };
		std::ifstream               in(path);
		if (!in) {
			logger::info("No {} - using defaults", path.string());
			return;
		}

		std::string line;
		while (std::getline(in, line)) {
			line = Trim(line);
			if (line.empty() || line[0] == ';' || line[0] == '#' || line[0] == '[') {
				continue;
			}
			const auto eq = line.find('=');
			if (eq == std::string::npos) {
				continue;
			}
			const auto key = Trim(line.substr(0, eq));
			const auto value = Trim(line.substr(eq + 1));
			try {
				if (EqualsNoCase(key, "bEnabled")) {
					enabled = value != "0" && !EqualsNoCase(value, "false");
				} else if (EqualsNoCase(key, "fMaxDistance")) {
					maxDistance = std::stof(value);
				} else if (EqualsNoCase(key, "iUpdateIntervalMs")) {
					updateIntervalMs = std::max<std::uint32_t>(100, static_cast<std::uint32_t>(std::stoul(value)));
				} else if (EqualsNoCase(key, "iMaxShown")) {
					maxShown = static_cast<std::uint32_t>(std::stoul(value));
				} else if (EqualsNoCase(key, "bGridObjects")) {
					gridObjects = value != "0" && !EqualsNoCase(value, "false");
				} else if (EqualsNoCase(key, "bWaterPlanes")) {
					waterPlanes = value != "0" && !EqualsNoCase(value, "false");
				} else if (EqualsNoCase(key, "fNearGridDistance")) {
					nearGridDistance = std::stof(value);
				} else if (EqualsNoCase(key, "fFarGridDistance")) {
					farGridDistance = std::stof(value);
				} else if (EqualsNoCase(key, "bAnimateExperimental")) {
					animate = value != "0" && !EqualsNoCase(value, "false");
				} else if (EqualsNoCase(key, "iAnimationFps")) {
					animationFps = std::min<std::uint32_t>(120, static_cast<std::uint32_t>(std::stoul(value)));
				} else if (EqualsNoCase(key, "bVerboseLog")) {
					verboseLog = value != "0" && !EqualsNoCase(value, "false");
				}
			} catch (const std::exception&) {
				logger::warn("AnvilLOD.ini: bad value for {}: {}", key, value);
			}
		}
		logger::info("Settings: enabled={} maxDistance={} interval={}ms maxShown={} gridObjects={} water={} near={} far={} animate={} animationFps={}",
			enabled, maxDistance, updateIntervalMs, maxShown, gridObjects, waterPlanes, nearGridDistance, farGridDistance, animate, animationFps);
	}

	bool Settings::Save() const
	{
		return IniFile::Write("Data/SKSE/Plugins/AnvilLOD.ini", "Dynamic", {
			{ "bEnabled", enabled ? "1" : "0" },
			{ "fMaxDistance", std::format("{:.0f}", maxDistance) },
			{ "iUpdateIntervalMs", std::to_string(updateIntervalMs) },
			{ "iMaxShown", std::to_string(maxShown) },
			{ "bGridObjects", gridObjects ? "1" : "0" },
			{ "bWaterPlanes", waterPlanes ? "1" : "0" },
			{ "fNearGridDistance", std::format("{:.0f}", nearGridDistance) },
			{ "fFarGridDistance", std::format("{:.0f}", farGridDistance) },
			{ "bAnimateExperimental", animate ? "1" : "0" },
			{ "iAnimationFps", std::to_string(animationFps) },
			{ "bVerboseLog", verboseLog ? "1" : "0" },
		});
	}
}
