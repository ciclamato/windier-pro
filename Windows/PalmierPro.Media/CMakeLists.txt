cmake_minimum_required(VERSION 3.25)
project(PalmierPro.Media LANGUAGES CXX)

option(PALMIER_ENABLE_FFMPEG "Build the native FFmpeg media adapter" ON)
set(PALMIER_FFMPEG_ROOT "" CACHE PATH "FFmpeg development prefix")

add_library(PalmierPro.Media SHARED
    src/palmier_media.cpp
)
target_compile_features(PalmierPro.Media PRIVATE cxx_std_20)
target_include_directories(PalmierPro.Media PUBLIC include)
target_compile_definitions(PalmierPro.Media PRIVATE PALMIER_MEDIA_EXPORTS)

if(PALMIER_ENABLE_FFMPEG)
    find_path(FFMPEG_INCLUDE_DIR libavformat/avformat.h
        HINTS "${PALMIER_FFMPEG_ROOT}/include")
    find_library(AVFORMAT_LIBRARY avformat
        HINTS "${PALMIER_FFMPEG_ROOT}/lib")
    find_library(AVCODEC_LIBRARY avcodec
        HINTS "${PALMIER_FFMPEG_ROOT}/lib")
    find_library(AVUTIL_LIBRARY avutil
        HINTS "${PALMIER_FFMPEG_ROOT}/lib")
    if(FFMPEG_INCLUDE_DIR AND AVFORMAT_LIBRARY AND AVCODEC_LIBRARY AND AVUTIL_LIBRARY)
        target_compile_definitions(PalmierPro.Media PRIVATE PALMIER_HAVE_FFMPEG=1)
        target_include_directories(PalmierPro.Media PRIVATE "${FFMPEG_INCLUDE_DIR}")
        target_link_libraries(PalmierPro.Media PRIVATE "${AVFORMAT_LIBRARY}" "${AVCODEC_LIBRARY}" "${AVUTIL_LIBRARY}")
    else()
        message(STATUS "FFmpeg development libraries not found; building a diagnostic stub")
    endif()
endif()

if(MSVC)
    target_compile_options(PalmierPro.Media PRIVATE /W4 /permissive-)
else()
    target_compile_options(PalmierPro.Media PRIVATE -Wall -Wextra -Wpedantic)
endif()
