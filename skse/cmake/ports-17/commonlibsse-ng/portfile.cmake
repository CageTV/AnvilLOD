# Skyrim 1.7.x build line: CommonLibSSE-NG 7.2.0 (alandtse, ng branch), pinned to the v7.2.0 commit.
# Fetched with git (commit pin) rather than a GitHub tarball hash, which GitHub re-compresses over time.
# VR is off on this line. Patch-safety is off because it downloads hde64 at configure time and vcpkg
# builds ports offline.
vcpkg_from_git(
    OUT_SOURCE_PATH SOURCE_PATH
    URL https://github.com/alandtse/CommonLibSSE-NG
    REF 7a60f4de794095d7b0f8928d1b930a52e9a7da83
    FETCH_REF ng
    HEAD_REF ng
)

vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -DBUILD_TESTS=off
        -DSKSE_SUPPORT_XBYAK=on
        -DSKSE_SUPPORT_PATCH_SAFETY=off
        -DENABLE_SKYRIM_SE=on
        -DENABLE_SKYRIM_AE=on
        -DENABLE_SKYRIM_VR=off
)

vcpkg_cmake_install()
vcpkg_cmake_config_fixup(PACKAGE_NAME CommonLibSSE CONFIG_PATH lib/cmake/CommonLibSSE)
vcpkg_copy_pdbs()

file(INSTALL "${SOURCE_PATH}/cmake/CommonLibSSE.cmake" DESTINATION "${CURRENT_PACKAGES_DIR}/share/CommonLibSSE")
file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include")
file(INSTALL "${SOURCE_PATH}/COPYING" DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}" RENAME copyright)
