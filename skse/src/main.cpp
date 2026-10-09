#include "Controller.h"
#include "LodDistances.h"
#include "Menu.h"
#include "Settings.h"

namespace
{
	void SetupLog()
	{
		auto dir = logger::log_directory();
		if (!dir) {
			return;
		}
		auto path = *dir / "AnvilLOD.log";
		auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path.string(), true);
		auto log = std::make_shared<spdlog::logger>("global", std::move(sink));
		log->set_level(spdlog::level::info);
		log->flush_on(spdlog::level::info);
		spdlog::set_default_logger(std::move(log));
		spdlog::set_pattern("[%H:%M:%S.%e] [%l] %v");
	}

	void OnMessage(SKSE::MessagingInterface::Message* a_msg)
	{
		auto& controller = AnvilLOD::Controller::Get();
		switch (a_msg->type) {
		case SKSE::MessagingInterface::kDataLoaded:
			AnvilLOD::Settings::Get().Load();
			// Related runtime fixes: recommended alongside AnvilLOD, never duplicated by it.
			logger::info("SkyrimLODFixes.dll (LOD Refresh Bug Fix): {}", AnvilLOD::LodDistances::PluginInstalled("SkyrimLODFixes.dll") ? "installed" : "not installed (recommended)");
			logger::info("DynDOLOD.dll: {}", AnvilLOD::LodDistances::PluginInstalled("DynDOLOD.dll") ? "installed" : "not installed");
			AnvilLOD::LodDistances::Get().Apply("startup");
			AnvilLOD::LodDistances::Get().StartWatching();
			controller.OnDataLoaded();
			controller.Start();
			AnvilLOD::Menu::Register();
			break;
		case SKSE::MessagingInterface::kPostLoadGame:
			AnvilLOD::LodDistances::Get().Apply("game loaded");
			break;
		case SKSE::MessagingInterface::kPreLoadGame:
		case SKSE::MessagingInterface::kNewGame:
			controller.Reset();
			break;
		default:
			break;
		}
	}
}

SKSEPluginLoad(const SKSE::LoadInterface* a_skse)
{
	// The two build lines link different CommonLibs (Skyrim 1.7 changed Address Library's file format and many
	// structs), and CommonLib's Init already opens Address Library, so the wrong DLL has to stop before Init.
	// SKSE logs the refusal in skse64.log.
	const auto game = a_skse->RuntimeVersion();
	// Line 1: SE 1.5.97 up to AE 1.6.1170. Line 17: everything newer (1.7.99, 1.7.104, ...).
	constexpr REL::Version kLastOld{ 1, 6, 1170, 0 };
	constexpr REL::Version kFirstSE{ 1, 5, 0, 0 };   // Skyrim VR is 1.4.15; every SE/AE runtime is 1.5 or newer
#if ANVILLOD_RUNTIME_LINE == 17
	constexpr auto kLine = "newer than 1.6.1170 (Skyrim 1.7.x)";
	if (game <= kLastOld) return false;
#elif ANVILLOD_RUNTIME_LINE == 14
	constexpr auto kLine = "Skyrim VR 1.4.15";
	if (game >= kFirstSE) return false;
#else
	constexpr auto kLine = "SE 1.5.97 to AE 1.6.1170";
	if (game > kLastOld || game < kFirstSE) return false;
#endif

	SKSE::Init(a_skse);
	SetupLog();
	logger::info("AnvilLOD SKSE plugin {} loading. Build line: {}; game {}", ANVILLOD_VERSION_STRING, kLine, game.string());
	AnvilLOD::Menu::SetGameVersion(game.string());

	if (!SKSE::GetMessagingInterface()->RegisterListener(OnMessage)) {
		logger::critical("Could not register for SKSE messages");
		return false;
	}
	return true;
}
