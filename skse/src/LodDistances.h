#pragma once

namespace AnvilLOD
{
	/// LOD distance settings (fBlockLevel0Distance etc.), applied to the game's INI settings at runtime, like
	/// DynDOLOD DLL NG does. Read in this order, later files win:
	///   Data\MCM\Config\DynDOLOD\settings.ini   (DynDOLOD DLL NG defaults, [Settings])
	///   Data\MCM\Settings\DynDOLOD.ini          (values saved by the DynDOLOD MCM, [Settings])
	///   Data\SKSE\Plugins\AnvilLOD.ini          ([LOD])
	/// so existing DynDOLOD MCM settings keep working after DynDOLOD.dll is removed. The files are watched while
	/// playing: edit and save one, and the change is applied within a couple of seconds.
	class LodDistances
	{
	public:
		static LodDistances& Get();

		/// True if Data\SKSE\Plugins\<a_dll> exists (SKSE loads every DLL there).
		static bool PluginInstalled(const char* a_dll);

		void Apply(const char* a_reason);   // read the files and set the game settings (main thread)
		void StartWatching();               // re-apply when one of the files changes

		/// The settings the in-game menu shows, in order: file key, slider range, display format.
		struct Entry
		{
			const char* group;
			const char* key;
			float       min;
			float       max;
			const char* format;
			const char* help;
		};
		static const std::vector<Entry>& Entries();

		std::optional<float> GetGame(const std::string& a_key) const;  // current value in the game
		bool                 SetGame(const std::string& a_key, float a_value);
		/// Writes the game's current values into AnvilLOD.ini [LOD], so they're used from now on.
		bool                 SaveToIni();
		/// True when DynDOLOD.dll is installed and AnvilLOD leaves these settings to it.
		bool                 DeferringToDynDolod() const;

	private:
		std::map<std::string, float> Read() const;
		std::filesystem::file_time_type NewestWrite() const;

		std::atomic_bool                _watching{ false };
		std::filesystem::file_time_type _lastWrite{};
	};
}
