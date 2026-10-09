# Skyrim VR build line: CommonLibSSE-NG 7.2.0 (alandtse, ng branch), pinned to the v7.2.0 commit, built for VR only
# (Address Library for SKSEVR, game version 1.4.15). Same pin as cmake/ports-17.
# Fetched with git (commit pin) rather than a GitHub tarball hash, which GitHub re-compresses over time.
# Patch-safety is off because it downloads hde64 at configure time and vcpkg builds ports offline.
vcpkg_from_git(
    OUT_SOURCE_PATH SOURCE_PATH
    URL https://github.com/alandtse/CommonLibSSE-NG
    REF 7a60f4de794095d7b0f8928d1b930a52e9a7da83
    FETCH_REF ng
    HEAD_REF ng
)

# CommonLibSSE-NG's VR build needs the OpenVR headers and openvr_api.lib, which it carries as a git submodule
# (extern/openvr). vcpkg_from_git does not fetch submodules, so fetch the submodule's pinned commit here.
vcpkg_from_git(
    OUT_SOURCE_PATH OPENVR_SOURCE_PATH
    URL https://github.com/ValveSoftware/openvr.git
    REF 60eb187801956ad277f1cae6680e3a410ee0873b
)
file(REMOVE_RECURSE "${SOURCE_PATH}/extern/openvr")
file(COPY "${OPENVR_SOURCE_PATH}/" DESTINATION "${SOURCE_PATH}/extern/openvr")

vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -DBUILD_TESTS=off
        -DSKSE_SUPPORT_XBYAK=on
        -DSKSE_SUPPORT_PATCH_SAFETY=off
        -DENABLE_SKYRIM_SE=off
        -DENABLE_SKYRIM_AE=off
        -DENABLE_SKYRIM_VR=on
)

vcpkg_cmake_install()

# CommonLib's public headers include <openvr.h>, so the plugin build needs the OpenVR headers and import library too.
file(INSTALL "${OPENVR_SOURCE_PATH}/headers/" DESTINATION "${CURRENT_PACKAGES_DIR}/include")
file(INSTALL "${OPENVR_SOURCE_PATH}/lib/win64/openvr_api.lib" DESTINATION "${CURRENT_PACKAGES_DIR}/lib")
vcpkg_cmake_config_fixup(PACKAGE_NAME CommonLibSSE CONFIG_PATH lib/cmake/CommonLibSSE)
vcpkg_copy_pdbs()

file(INSTALL "${SOURCE_PATH}/cmake/CommonLibSSE.cmake" DESTINATION "${CURRENT_PACKAGES_DIR}/share/CommonLibSSE")
file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include")
file(INSTALL "${SOURCE_PATH}/COPYING" DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}" RENAME copyright)
