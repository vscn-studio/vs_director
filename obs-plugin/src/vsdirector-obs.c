#include <obs-module.h>
#include <util/platform.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

/* Keep the module loadable by the installed OBS 32.0.x while compiling
 * against the repository's append-only 32.x source-info declarations. */
#undef LIBOBS_API_VER
#define LIBOBS_API_VER MAKE_SEMANTIC_VERSION(32, 0, 0)

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("vsdirector-obs", "en-US")

#define VSD_PROTOCOL_VERSION 1
#define VSD_HEADER_BYTES 512
#define VSD_AUDIO_CHUNK_FRAMES 480
#define VSD_OBS_32_0_SOURCE_INFO_SIZE 408U
#define VSD_PATH_BYTES 4096

#pragma pack(push, 1)
struct vsd_header {
    char magic[8];
    int32_t version;
    int32_t header_bytes;
    int32_t width;
    int32_t height;
    int32_t stride;
    int32_t pixel_format;
    int32_t video_slot_count;
    int32_t video_frame_bytes;
    volatile int32_t active_video_slot;
    volatile int32_t producer_state;
    volatile int64_t video_sequence;
    volatile int64_t video_timestamp_ns;
    volatile int64_t producer_heartbeat_ms;
    volatile int64_t consumer_heartbeat_ms;
    volatile int64_t dropped_video_frames;
    int32_t audio_sample_rate;
    int32_t audio_channels;
    int32_t audio_capacity_frames;
    int32_t audio_data_offset;
    volatile int64_t audio_write_frame;
    volatile int64_t audio_sequence;
    volatile int64_t audio_producer_heartbeat_ms;
    volatile int64_t audio_dropped_frames;
    uint8_t reserved[VSD_HEADER_BYTES - 136];
};
#pragma pack(pop)

struct vsd_source {
    obs_source_t *source;
    struct vsd_header *header;
    uint8_t *mapping;
    size_t mapping_size;
    uint8_t *video_copy;
    size_t video_copy_size;
    int16_t *audio_copy;
    size_t audio_copy_frames;
    int64_t last_video_sequence;
    int64_t audio_read_frame;
    uint64_t next_open_attempt_ns;
#ifdef _WIN32
    HANDLE map_handle;
#else
    int map_fd;
#endif
};

static int64_t atomic_read_i64(volatile int64_t *value)
{
#ifdef _WIN32
    return InterlockedCompareExchange64((volatile LONG64 *)value, 0, 0);
#else
    return __atomic_load_n(value, __ATOMIC_ACQUIRE);
#endif
}

static int32_t atomic_read_i32(volatile int32_t *value)
{
#ifdef _WIN32
    return InterlockedCompareExchange((volatile LONG *)value, 0, 0);
#else
    return __atomic_load_n(value, __ATOMIC_ACQUIRE);
#endif
}

static void atomic_write_i64(volatile int64_t *value, int64_t next)
{
#ifdef _WIN32
    InterlockedExchange64((volatile LONG64 *)value, next);
#else
    __atomic_store_n(value, next, __ATOMIC_RELEASE);
#endif
}

static int64_t monotonic_milliseconds(void)
{
    return (int64_t)(os_gettime_ns() / 1000000ULL);
}

#ifndef _WIN32
static bool vsd_mapping_path(char *path, size_t path_size)
{
    const char *temporary_directory = getenv("TMPDIR");
    if (!temporary_directory || !temporary_directory[0])
        temporary_directory = "/tmp";

    size_t length = strlen(temporary_directory);
    const char *separator = length > 0 && temporary_directory[length - 1] == '/'
                                ? ""
                                : "/";
    int written = snprintf(path, path_size, "%s%svsdirector-live.ipc",
                           temporary_directory, separator);
    return written > 0 && (size_t)written < path_size;
}
#endif

static void vsd_close_mapping(struct vsd_source *context)
{
    if (!context)
        return;
    context->header = NULL;
    if (context->mapping) {
#ifdef _WIN32
        UnmapViewOfFile(context->mapping);
#else
        munmap(context->mapping, context->mapping_size);
#endif
        context->mapping = NULL;
    }
#ifdef _WIN32
    if (context->map_handle) {
        CloseHandle(context->map_handle);
        context->map_handle = NULL;
    }
#else
    if (context->map_fd >= 0) {
        close(context->map_fd);
        context->map_fd = -1;
    }
#endif
    context->mapping_size = 0;
    context->last_video_sequence = 0;
    context->audio_read_frame = 0;
}

static bool vsd_validate_mapping(struct vsd_source *context)
{
    struct vsd_header *header = context->header;
    if (!header || context->mapping_size < VSD_HEADER_BYTES)
        return false;
    if (memcmp(header->magic, "VSDIRIPC", 8) != 0 ||
        header->version != VSD_PROTOCOL_VERSION ||
        header->header_bytes != VSD_HEADER_BYTES ||
        header->width < 16 || header->height < 16 ||
        header->stride != header->width * 4 ||
        header->pixel_format != 1 ||
        header->video_slot_count != 2 ||
        header->video_frame_bytes != header->stride * header->height ||
        header->audio_sample_rate != 48000 ||
        header->audio_channels != 2 ||
        header->audio_capacity_frames <= 0)
        return false;

    size_t required = (size_t)header->audio_data_offset +
        (size_t)header->audio_capacity_frames * 2U * sizeof(int16_t);
    return required <= context->mapping_size;
}

static bool vsd_open_mapping(struct vsd_source *context)
{
    if (context->mapping)
        return vsd_validate_mapping(context);

#ifdef _WIN32
    context->map_handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE,
                                           L"Local\\VSDirector.Live");
    if (!context->map_handle)
        return false;
    context->mapping = MapViewOfFile(context->map_handle, FILE_MAP_ALL_ACCESS,
                                     0, 0, 0);
    if (!context->mapping) {
        vsd_close_mapping(context);
        return false;
    }
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery(context->mapping, &info, sizeof(info))) {
        vsd_close_mapping(context);
        return false;
    }
    context->mapping_size = info.RegionSize;
#else
    char mapping_path[VSD_PATH_BYTES];
    if (!vsd_mapping_path(mapping_path, sizeof(mapping_path)))
        return false;
    context->map_fd = open(mapping_path, O_RDWR);
    if (context->map_fd < 0)
        return false;
    struct stat info;
    if (fstat(context->map_fd, &info) != 0 || info.st_size < VSD_HEADER_BYTES) {
        vsd_close_mapping(context);
        return false;
    }
    context->mapping_size = (size_t)info.st_size;
    context->mapping = mmap(NULL, context->mapping_size,
                            PROT_READ | PROT_WRITE, MAP_SHARED,
                            context->map_fd, 0);
    if (context->mapping == MAP_FAILED) {
        context->mapping = NULL;
        vsd_close_mapping(context);
        return false;
    }
#endif

    context->header = (struct vsd_header *)context->mapping;
    if (!vsd_validate_mapping(context)) {
        vsd_close_mapping(context);
        return false;
    }
    context->audio_read_frame = atomic_read_i64(&context->header->audio_write_frame);
    return true;
}

static bool ensure_video_buffer(struct vsd_source *context, size_t bytes)
{
    if (context->video_copy_size >= bytes)
        return true;
    uint8_t *next = brealloc(context->video_copy, bytes);
    if (!next)
        return false;
    context->video_copy = next;
    context->video_copy_size = bytes;
    return true;
}

static bool ensure_audio_buffer(struct vsd_source *context, size_t frames)
{
    if (context->audio_copy_frames >= frames)
        return true;
    int16_t *next = brealloc(context->audio_copy,
                             frames * 2U * sizeof(int16_t));
    if (!next)
        return false;
    context->audio_copy = next;
    context->audio_copy_frames = frames;
    return true;
}

static void vsd_output_video(struct vsd_source *context)
{
    struct vsd_header *header = context->header;
    int64_t sequence = atomic_read_i64(&header->video_sequence);
    if (sequence <= 0 || sequence == context->last_video_sequence)
        return;

    int slot = atomic_read_i32(&header->active_video_slot);
    size_t bytes = (size_t)header->video_frame_bytes;
    if (slot < 0 || slot >= header->video_slot_count ||
        !ensure_video_buffer(context, bytes))
        return;

    uint8_t *source = context->mapping + VSD_HEADER_BYTES + bytes * (size_t)slot;
    memcpy(context->video_copy, source, bytes);
    if (sequence != atomic_read_i64(&header->video_sequence))
        return;

    struct obs_source_frame frame = {0};
    frame.data[0] = context->video_copy;
    frame.linesize[0] = (uint32_t)header->stride;
    frame.width = (uint32_t)header->width;
    frame.height = (uint32_t)header->height;
    frame.timestamp = (uint64_t)atomic_read_i64(&header->video_timestamp_ns);
    frame.format = VIDEO_FORMAT_BGRA;
    frame.full_range = true;
    frame.flip = true;
    obs_source_output_video(context->source, &frame);
    context->last_video_sequence = sequence;
}

static void copy_audio_ring(struct vsd_source *context, int64_t first_frame,
                            size_t frames)
{
    struct vsd_header *header = context->header;
    size_t capacity = (size_t)header->audio_capacity_frames;
    size_t first = (size_t)(first_frame % (int64_t)capacity);
    size_t first_count = frames < capacity - first ? frames : capacity - first;
    int16_t *ring = (int16_t *)(context->mapping + header->audio_data_offset);
    memcpy(context->audio_copy, ring + first * 2U,
           first_count * 2U * sizeof(int16_t));
    if (frames > first_count) {
        memcpy(context->audio_copy + first_count * 2U, ring,
               (frames - first_count) * 2U * sizeof(int16_t));
    }
}

static void vsd_output_audio(struct vsd_source *context)
{
    struct vsd_header *header = context->header;
    int64_t write_frame = atomic_read_i64(&header->audio_write_frame);
    int64_t available = write_frame - context->audio_read_frame;
    if (available <= 0)
        return;
    if (available > header->audio_capacity_frames) {
        context->audio_read_frame = write_frame - header->audio_capacity_frames;
        available = header->audio_capacity_frames;
    }

    while (available > 0) {
        size_t frames = (size_t)(available > VSD_AUDIO_CHUNK_FRAMES
                                     ? VSD_AUDIO_CHUNK_FRAMES
                                     : available);
        if (!ensure_audio_buffer(context, frames))
            return;
        copy_audio_ring(context, context->audio_read_frame, frames);

        struct obs_source_audio audio = {0};
        audio.data[0] = (uint8_t *)context->audio_copy;
        audio.frames = (uint32_t)frames;
        audio.speakers = SPEAKERS_STEREO;
        audio.samples_per_sec = (uint32_t)header->audio_sample_rate;
        audio.format = AUDIO_FORMAT_16BIT;
        audio.timestamp = os_gettime_ns() -
            (uint64_t)available * 1000000000ULL /
                (uint64_t)header->audio_sample_rate;
        obs_source_output_audio(context->source, &audio);
        context->audio_read_frame += (int64_t)frames;
        available -= (int64_t)frames;
    }
}

static const char *vsd_get_name(void *unused)
{
    UNUSED_PARAMETER(unused);
    return "VS Director Offscreen";
}

static void *vsd_create(obs_data_t *settings, obs_source_t *source)
{
    UNUSED_PARAMETER(settings);
    struct vsd_source *context = bzalloc(sizeof(*context));
    context->source = source;
#ifndef _WIN32
    context->map_fd = -1;
#endif
    return context;
}

static void vsd_destroy(void *data)
{
    struct vsd_source *context = data;
    if (!context)
        return;
    vsd_close_mapping(context);
    bfree(context->video_copy);
    bfree(context->audio_copy);
    bfree(context);
}

static void vsd_tick(void *data, float seconds)
{
    UNUSED_PARAMETER(seconds);
    struct vsd_source *context = data;
    if (!context)
        return;

    uint64_t now = os_gettime_ns();
    if (!context->mapping) {
        if (now < context->next_open_attempt_ns)
            return;
        context->next_open_attempt_ns = now + 500000000ULL;
        if (!vsd_open_mapping(context))
            return;
    }

    if (!vsd_validate_mapping(context) ||
        atomic_read_i32(&context->header->producer_state) == 0) {
        vsd_close_mapping(context);
        return;
    }

    atomic_write_i64(&context->header->consumer_heartbeat_ms,
                     monotonic_milliseconds());
    vsd_output_video(context);
    vsd_output_audio(context);
}

static uint32_t vsd_width(void *data)
{
    struct vsd_source *context = data;
    return context && context->header ? (uint32_t)context->header->width : 1920U;
}

static uint32_t vsd_height(void *data)
{
    struct vsd_source *context = data;
    return context && context->header ? (uint32_t)context->header->height : 1080U;
}

static struct obs_source_info vsd_source_info = {
    .id = "vsdirector_offscreen_source",
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_ASYNC_VIDEO | OBS_SOURCE_AUDIO,
    .get_name = vsd_get_name,
    .create = vsd_create,
    .destroy = vsd_destroy,
    .video_tick = vsd_tick,
    .get_width = vsd_width,
    .get_height = vsd_height,
};

bool obs_module_load(void)
{
	obs_register_source_s(&vsd_source_info, VSD_OBS_32_0_SOURCE_INFO_SIZE);
	blog(LOG_INFO, "[VS Director] Offscreen video/audio source loaded");
	return true;
}
