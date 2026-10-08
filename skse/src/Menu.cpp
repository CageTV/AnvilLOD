#include "Menu.h"

#include "Controller.h"
#include "LodDistances.h"
#include "Settings.h"

#include <Windows.h>

namespace AnvilLOD::Menu
{
	// A small client for SKSE Menu Framework's exports (the same names its public consumer header uses:
	// AddSectionItem plus the cimgui "ig*" functions). Resolved by name at runtime, so AnvilLOD has no link-time
	// dependency on the framework and works without it. Every call is null-checked.
	namespace Smf
	{
		struct ImVec2
		{
			float x, y;
		};
		using RenderFunction = void(__stdcall*)();

		HMODULE Module()
		{
			static HMODULE module = nullptr;
			if (!module) module = ::GetModuleHandleW(L"SKSEMenuFramework");
			return module;
		}

		template <class T>
		T Fn(const char* a_name)
		{
			auto m = Module();
			return m ? reinterpret_cast<T>(::GetProcAddress(m, a_name)) : nullptr;
		}

		bool AddSectionItem(const char* a_path, RenderFunction a_render)
		{
			static auto f = Fn<void (*)(const char*, RenderFunction)>("AddSectionItem");
			if (!f) return false;
			f(a_path, a_render);
			return true;
		}

		float Version()
		{
			static auto f = Fn<float (*)()>("GetMenuFrameworkVersion");
			return f ? f() : 0.0f;
		}

		void Text(const char* a_fmt, ...)
		{
			static auto f = Fn<void (*)(const char*, va_list)>("igTextV");
			if (!f) return;
			va_list args;
			va_start(args, a_fmt);
			f(a_fmt, args);
			va_end(args);
		}

		void TextWrapped(const char* a_fmt, ...)
		{
			static auto f = Fn<void (*)(const char*, va_list)>("igTextWrappedV");
			if (!f) return;
			va_list args;
			va_start(args, a_fmt);
			f(a_fmt, args);
			va_end(args);
		}

		void TextDisabled(const char* a_fmt, ...)
		{
			static auto f = Fn<void (*)(const char*, va_list)>("igTextDisabledV");
			if (!f) return;
			va_list args;
			va_start(args, a_fmt);
			f(a_fmt, args);
			va_end(args);
		}

		bool SliderFloat(const char* a_label, float* a_v, float a_min, float a_max, const char* a_format)
		{
			static auto f = Fn<bool (*)(const char*, float*, float, float, const char*, int)>("igSliderFloat");
			return f && f(a_label, a_v, a_min, a_max, a_format, 0);
		}

		bool SliderInt(const char* a_label, int* a_v, int a_min, int a_max)
		{
			static auto f = Fn<bool (*)(const char*, int*, int, int, const char*, int)>("igSliderInt");
			return f && f(a_label, a_v, a_min, a_max, "%d", 0);
		}

		bool Checkbox(const char* a_label, bool* a_v)
		{
			static auto f = Fn<bool (*)(const char*, bool*)>("igCheckbox");
			return f && f(a_label, a_v);
		}

		bool Button(const char* a_label)
		{
			static auto f = Fn<bool (*)(const char*, ImVec2)>("igButton");
			return f && f(a_label, ImVec2{ 0.0f, 0.0f });
		}

		void SameLine()
		{
			static auto f = Fn<void (*)(float, float)>("igSameLine");
			if (f) f(0.0f, -1.0f);
		}

		void Separator()
		{
			static auto f = Fn<void (*)()>("igSeparator");
			if (f) f();
		}

		void SeparatorText(const char* a_label)
		{
			static auto f = Fn<void (*)(const char*)>("igSeparatorText");
			if (f) f(a_label);
			else Text("%s", a_label);
		}
	}

	namespace
	{
		std::string g_status;       // last save result, shown under the buttons
		std::string g_gameVersion;  // from SKSEPluginLoad

		void __stdcall RenderLodDistances()
		{
			auto& lod = LodDistances::Get();
			Smf::TextWrapped("LOD, fade and grass distances, applied to the game straight away. The LOD distances are the ones DynDOLOD's "
							 "MCM changes; values saved in DynDOLOD's MCM file are read at startup, and saving here "
							 "writes them to SKSE\\Plugins\\AnvilLOD.ini, which wins from then on.");
			if (lod.DeferringToDynDolod()) {
				Smf::TextWrapped("DynDOLOD.dll is installed and manages these settings. Changes here last until "
								 "DynDOLOD applies its own again. Set bOverrideDynDOLOD=1 in AnvilLOD.ini to let AnvilLOD take over.");
			}
			Smf::Separator();
			const char* group = nullptr;
			for (const auto& e : LodDistances::Entries()) {
				if (!group || std::strcmp(group, e.group) != 0) {
					group = e.group;
					Smf::SeparatorText(group);
					if (std::strcmp(group, "Grass") == 0) {
						Smf::TextDisabled("How far real grass reaches is iGrassCellRadius in Skyrim.ini [Grass] (read at startup, not changeable here).");
					}
				}
				auto cur = lod.GetGame(e.key);
				if (!cur) {
					Smf::TextDisabled("%s: not available in this game version", e.key);
					continue;
				}
				float v = *cur;
				if (Smf::SliderFloat(e.key, &v, e.min, e.max, e.format)) lod.SetGame(e.key, v);
				Smf::TextDisabled("    %s", e.help);
			}
			Smf::Separator();
			if (Smf::Button("Save to AnvilLOD.ini")) g_status = lod.SaveToIni() ? "Saved." : "Could not write AnvilLOD.ini (see AnvilLOD.log).";
			Smf::SameLine();
			if (Smf::Button("Reload from files")) {
				lod.Apply("menu reload");
				g_status = "Reloaded.";
			}
			if (!g_status.empty()) Smf::Text("%s", g_status.c_str());
			Smf::TextDisabled("Block distances take effect as LOD blocks reload; moving a few cells or a load screen shows them fully.");
		}

		void __stdcall RenderDynamic()
		{
			auto& s = Settings::Get();
			auto& c = Controller::Get();
			Smf::TextWrapped("Dynamic LOD draws the LOD of references that quests switch on and off (Helgen Reborn's "
							 "town, for example), only while they're enabled.");
			Smf::Text("Switchable references: %zu, grid objects: %zu (in %zu worldspaces); %zu drawn now", c.ItemCount() - c.GridCount(), c.GridCount(), c.WorldCount(), c.ShownCount());
			Smf::Separator();
			Smf::Checkbox("Enabled", &s.enabled);
			Smf::SliderFloat("Max distance", &s.maxDistance, 20000.0f, 400000.0f, "%.0f");
			int maxShown = static_cast<int>(s.maxShown);
			if (Smf::SliderInt("Max objects drawn", &maxShown, 100, 20000)) s.maxShown = static_cast<std::uint32_t>(maxShown);
			int interval = static_cast<int>(s.updateIntervalMs);
			if (Smf::SliderInt("Update interval (ms)", &interval, 100, 5000)) s.updateIntervalMs = static_cast<std::uint32_t>(interval);
			Smf::Checkbox("Verbose log", &s.verboseLog);
			Smf::Separator();
			if (Smf::Button("Save to AnvilLOD.ini##dyn")) g_status = s.Save() ? "Saved." : "Could not write AnvilLOD.ini.";
			if (!g_status.empty()) Smf::Text("%s", g_status.c_str());
		}

		void __stdcall RenderGridObjects()
		{
			auto& s = Settings::Get();
			auto& c = Controller::Get();
			Smf::TextWrapped("Water planes, waterfalls, creeks, fires, windmills and ships: objects DynDOLOD's rules mark with a "
							 "Grid and no static LOD. AnvilLOD draws them beyond the loaded cells and keeps them animated. "
							 "Mods' _dyndolod_lod meshes (CS Water Mod, DynDOLOD Resources) are used when present.");
			if (c.GridCount() == 0) {
				Smf::TextDisabled("No grid objects in AnvilLOD.dyn - generate with \"Water and animated distant objects\" ticked.");
			}
			Smf::Text("Grid objects: %zu, animated now: %zu", c.GridCount(), c.AnimatedCount());
			Smf::Separator();
			Smf::Checkbox("Draw grid objects", &s.gridObjects);
			Smf::Checkbox("Include water-shader planes (lakes, pools, streams)", &s.waterPlanes);
			Smf::SliderFloat("Near grid distance", &s.nearGridDistance, 8192.0f, 200000.0f, "%.0f");
			Smf::TextDisabled("    Water planes, creeks, rapids, fires (DynDOLOD \"Near LOD\"). 4096 = one cell.");
			Smf::SliderFloat("Far grid distance", &s.farGridDistance, 20000.0f, 400000.0f, "%.0f");
			Smf::TextDisabled("    Waterfalls, windmills, water wheels, ships (DynDOLOD \"Far LOD\" / \"Far Full\").");
			Smf::Checkbox("Animate them (EXPERIMENTAL - may crash)", &s.animate);
			Smf::TextDisabled("    Waterfall flow and windmill blades. Takes effect for objects drawn after the change (use Redraw now).");
			int fps = static_cast<int>(s.animationFps);
			if (Smf::SliderInt("Animation ticks per second", &fps, 5, 60)) s.animationFps = static_cast<std::uint32_t>(fps);
			Smf::Separator();
			if (Smf::Button("Redraw now")) {
				c.Refresh();
				g_status = "Redrawing with the current settings.";
			}
			Smf::SameLine();
			if (Smf::Button("Save to AnvilLOD.ini##grid")) g_status = s.Save() ? "Saved." : "Could not write AnvilLOD.ini.";
			if (!g_status.empty()) Smf::Text("%s", g_status.c_str());
		}

		void __stdcall RenderAbout()
		{
			Smf::Text("AnvilLOD %s", ANVILLOD_VERSION_STRING);
#if ANVILLOD_RUNTIME_LINE == 17
			Smf::Text("Build: game versions newer than 1.6.1170 (Skyrim 1.7.x)");
#else
			Smf::Text("Build: game versions SE 1.5.97 to AE 1.6.1170");
#endif
			Smf::Text("Game version: %s", g_gameVersion.c_str());
			Smf::Separator();
			Smf::Text("LOD Refresh Bug Fix (SkyrimLODFixes.dll): %s",
				LodDistances::PluginInstalled("SkyrimLODFixes.dll") ? "installed" : "not installed (recommended)");
			Smf::Text("DynDOLOD.dll: %s", LodDistances::PluginInstalled("DynDOLOD.dll") ? "installed" : "not installed");
			Smf::Separator();
			Smf::TextDisabled("Log: Documents\\My Games\\Skyrim Special Edition\\SKSE\\AnvilLOD.log");
		}
	}

	void SetGameVersion(std::string a_version) { g_gameVersion = std::move(a_version); }

	void Register()
	{
		if (!Smf::Module()) {
			logger::info("No SKSE Menu Framework (or compatible) found; the in-game menu is off. Settings are in AnvilLOD.ini.");
			return;
		}
		const bool ok = Smf::AddSectionItem("AnvilLOD/Distances & Grass", RenderLodDistances)
					 && Smf::AddSectionItem("AnvilLOD/Dynamic LOD", RenderDynamic)
					 && Smf::AddSectionItem("AnvilLOD/Water & Animated Objects", RenderGridObjects)
					 && Smf::AddSectionItem("AnvilLOD/About", RenderAbout);
		logger::info("SKSE Menu Framework {:.1f}: {}", Smf::Version(), ok ? "AnvilLOD pages added" : "AddSectionItem not exported, menu off");
	}
}
