#include "DataFile.h"

namespace AnvilLOD
{
	namespace
	{
		class Reader
		{
		public:
			explicit Reader(std::vector<char> a_data) :
				_data(std::move(a_data)) {}

			template <class T>
			bool Get(T& a_out)
			{
				if (_pos + sizeof(T) > _data.size()) {
					return false;
				}
				std::memcpy(&a_out, _data.data() + _pos, sizeof(T));
				_pos += sizeof(T);
				return true;
			}

			bool GetString(std::string& a_out)
			{
				std::uint16_t len = 0;
				if (!Get(len) || _pos + len > _data.size()) {
					return false;
				}
				a_out.assign(_data.data() + _pos, len);
				_pos += len;
				return true;
			}

		private:
			std::vector<char> _data;
			std::size_t       _pos{ 0 };
		};
	}

	bool DataFile::Read(const std::filesystem::path& a_path)
	{
		std::ifstream in(a_path, std::ios::binary);
		if (!in) {
			logger::info("{} not found - no dynamic LOD (generate LOD with AnvilLOD first)", a_path.string());
			return false;
		}
		std::vector<char> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
		Reader            r(std::move(bytes));

		std::array<char, 4> magic{};
		std::uint32_t       version = 0;
		if (!r.Get(magic) || std::string_view(magic.data(), 4) != "ALDY"sv || !r.Get(version)) {
			logger::error("{} is not an AnvilLOD dynamic LOD file", a_path.string());
			return false;
		}
		// Version 2 added grid-object flags; the layout is the same, so version 1 files still load.
		if (version < 1 || version > 2) {
			logger::error("{} has version {}, this plugin reads versions 1-2 - update the AnvilLOD plugin or regenerate", a_path.string(), version);
			return false;
		}

		std::uint32_t count = 0;
		if (!r.Get(count)) {
			return false;
		}
		strings.resize(count);
		for (auto& s : strings) {
			if (!r.GetString(s)) {
				logger::error("{}: truncated string table", a_path.string());
				return false;
			}
		}

		if (!r.Get(count)) {
			return false;
		}
		entries.resize(count);
		for (auto& e : entries) {
			if (!r.Get(e.refPlugin) || !r.Get(e.refLocalId) || !r.Get(e.worldPlugin) || !r.Get(e.worldLocalId) || !r.Get(e.mesh) ||
				!r.Get(e.pos) || !r.Get(e.rot) || !r.Get(e.scale) || !r.Get(e.parentPlugin) || !r.Get(e.parentLocalId) || !r.Get(e.flags)) {
				logger::error("{}: truncated entry table", a_path.string());
				return false;
			}
			const auto n = static_cast<std::uint32_t>(strings.size());
			if (e.refPlugin >= n || e.worldPlugin >= n || e.mesh >= n || (e.parentPlugin != DynamicEntry::kNone && e.parentPlugin >= n)) {
				logger::error("{}: bad string index", a_path.string());
				return false;
			}
		}
		return true;
	}
}
