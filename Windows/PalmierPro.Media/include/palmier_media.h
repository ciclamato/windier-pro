#pragma once

#if defined(_WIN32)
#  if defined(PALMIER_MEDIA_EXPORTS)
#    define PALMIER_MEDIA_API __declspec(dllexport)
#  else
#    define PALMIER_MEDIA_API __declspec(dllimport)
#  endif
#else
#  define PALMIER_MEDIA_API
#endif

extern "C" {

struct PalmierMediaProbeResult {
    double duration_seconds;
    int width;
    int height;
    double frame_rate;
    int has_video;
    int has_audio;
};

PALMIER_MEDIA_API int palmier_media_probe(const char* utf8_path, PalmierMediaProbeResult* result, char* error_message, int error_message_capacity);

}
