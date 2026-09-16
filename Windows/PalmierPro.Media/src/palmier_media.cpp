#include "palmier_media.h"

#include <algorithm>
#include <cstring>
#include <string>

#ifdef PALMIER_HAVE_FFMPEG
#include <libavformat/avformat.h>
#endif

namespace {
void set_error(char* destination, int capacity, const char* message) {
    if (!destination || capacity <= 0) return;
    const auto length = std::min(std::strlen(message), static_cast<size_t>(capacity - 1));
    std::memcpy(destination, message, length);
    destination[length] = '\0';
}
}

int palmier_media_probe(const char* utf8_path, PalmierMediaProbeResult* result, char* error_message, int error_message_capacity) {
    if (!utf8_path || !result) {
        set_error(error_message, error_message_capacity, "A media path and result buffer are required.");
        return -1;
    }
    *result = {};

#ifdef PALMIER_HAVE_FFMPEG
    AVFormatContext* context = nullptr;
    if (avformat_open_input(&context, utf8_path, nullptr, nullptr) < 0) {
        set_error(error_message, error_message_capacity, "FFmpeg could not open the media file.");
        return -2;
    }
    const int stream_error = avformat_find_stream_info(context, nullptr);
    if (stream_error < 0) {
        avformat_close_input(&context);
        set_error(error_message, error_message_capacity, "FFmpeg could not read stream information.");
        return -3;
    }
    if (context->duration != AV_NOPTS_VALUE) result->duration_seconds = static_cast<double>(context->duration) / AV_TIME_BASE;
    for (unsigned int index = 0; index < context->nb_streams; ++index) {
        const AVStream* stream = context->streams[index];
        if (!stream || !stream->codecpar) continue;
        if (stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
            result->has_video = 1;
            result->width = stream->codecpar->width;
            result->height = stream->codecpar->height;
            if (stream->avg_frame_rate.den != 0) result->frame_rate = av_q2d(stream->avg_frame_rate);
        } else if (stream->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
            result->has_audio = 1;
        }
    }
    avformat_close_input(&context);
    return 0;
#else
    (void)utf8_path;
    set_error(error_message, error_message_capacity, "This native build needs FFmpeg development libraries. Use the managed FFmpeg adapter or rebuild with PALMIER_FFMPEG_ROOT.");
    return -10;
#endif
}
