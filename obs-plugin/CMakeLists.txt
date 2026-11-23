cmake_minimum_required(VERSION 3.28)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
  project(vsdirector-obs VERSION 0.1.0 LANGUAGES C)
endif()

if(NOT TARGET OBS::libobs)
  find_package(libobs CONFIG REQUIRED)
endif()

add_library(vsdirector-obs MODULE src/vsdirector-obs.c)
target_compile_features(vsdirector-obs PRIVATE c_std_11)
target_link_libraries(vsdirector-obs PRIVATE OBS::libobs)
set_target_properties(vsdirector-obs PROPERTIES PREFIX "")

if(COMMAND set_target_properties_obs)
  # OBS' in-tree helper supplies the native plugin layout and embeds data files.
  set_target_properties_obs(vsdirector-obs PROPERTIES FOLDER plugins PREFIX "")
  if(APPLE)
    # OBS' in-tree helper defaults third-party modules to OBS' own identity.
    file(READ "${CMAKE_CURRENT_SOURCE_DIR}/modinfo.json" director_metadata)
    string(JSON director_version GET "${director_metadata}" version)
    string(REGEX REPLACE "-.*$" "" director_bundle_version "${director_version}")
    set_target_properties(vsdirector-obs PROPERTIES
      XCODE_ATTRIBUTE_PRODUCT_BUNDLE_IDENTIFIER "studio.vscn.vsdirector-obs"
      XCODE_ATTRIBUTE_MARKETING_VERSION "${director_bundle_version}"
      XCODE_ATTRIBUTE_CURRENT_PROJECT_VERSION "${director_bundle_version}"
      XCODE_ATTRIBUTE_INFOPLIST_KEY_NSHumanReadableCopyright "Copyright (c) 2026 HansJack (VSCN-Studio)"
    )
  endif()
elseif(APPLE)
  file(GLOB_RECURSE VSDIRECTOR_OBS_DATA CONFIGURE_DEPENDS data/*)
  foreach(data_file IN LISTS VSDIRECTOR_OBS_DATA)
    file(RELATIVE_PATH relative_path "${CMAKE_CURRENT_SOURCE_DIR}/data" "${data_file}")
    get_filename_component(resource_directory "${relative_path}" DIRECTORY)
    set_source_files_properties(
      "${data_file}"
      PROPERTIES MACOSX_PACKAGE_LOCATION "Resources/${resource_directory}"
    )
  endforeach()
  target_sources(vsdirector-obs PRIVATE ${VSDIRECTOR_OBS_DATA})
  set_target_properties(
    vsdirector-obs
    PROPERTIES
      BUNDLE TRUE
      BUNDLE_EXTENSION plugin
      MACOSX_BUNDLE_BUNDLE_NAME "VS Director OBS Source"
      MACOSX_BUNDLE_GUI_IDENTIFIER "studio.vscn.vsdirector-obs"
  )
  install(TARGETS vsdirector-obs BUNDLE DESTINATION .)
elseif(WIN32)
  install(
    TARGETS vsdirector-obs
    RUNTIME DESTINATION obs-plugins/64bit
    LIBRARY DESTINATION obs-plugins/64bit
  )
  install(DIRECTORY data/locale DESTINATION data/obs-plugins/vsdirector-obs)
else()
  include(GNUInstallDirs)
  install(TARGETS vsdirector-obs LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}/obs-plugins)
  install(DIRECTORY data/locale DESTINATION ${CMAKE_INSTALL_DATADIR}/obs/obs-plugins/vsdirector-obs)
endif()
