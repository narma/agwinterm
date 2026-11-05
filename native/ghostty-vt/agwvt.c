#define COBJMACROS
#include "agwvt.h"
#include <ghostty/vt.h>
#include <stdlib.h>
#include <string.h>
#include <malloc.h>
#include <stdatomic.h>
#include <windows.h>
#include <wincodec.h>

// Frozen upstream value structs; the adapter currently targets Windows x64.
_Static_assert(sizeof(GhosttyStyle) == 16, "GhosttyStyle ABI changed");
_Static_assert(sizeof(GhosttyGridRef) == 16, "GhosttyGridRef ABI changed");
_Static_assert(sizeof(GhosttySelection) == 40, "GhosttySelection ABI changed");
static uint32_t style_attributes(GhosttyStyle style) {
    return ((style.flags & GHOSTTY_STYLE_FLAG_BOLD) ? 1 : 0) |
        ((style.flags & GHOSTTY_STYLE_FLAG_ITALIC) ? 2 : 0) |
        (GHOSTTY_STYLE_UNDERLINE(style) ? 4 : 0) |
        ((style.flags & GHOSTTY_STYLE_FLAG_INVERSE) ? 8 : 0) |
        ((style.flags & GHOSTTY_STYLE_FLAG_FAINT) ? 16 : 0) |
        ((style.flags & GHOSTTY_STYLE_FLAG_STRIKETHROUGH) ? 32 : 0);
}

// Use Windows' PNG decoder without a managed callback or an extra runtime.
// Each invocation owns its COM objects; terminal input may arrive on any thread.
static bool decode_png(void* context, const GhosttyAllocator* allocator,
    const uint8_t* data, size_t length, GhosttySysImage* out) {
    (void)context;
    static const uint8_t signature[] = {137, 80, 78, 71, 13, 10, 26, 10};
    if (length < sizeof(signature) || length > 64u * 1024u * 1024u ||
        memcmp(data, signature, sizeof(signature))) return false;
    HRESULT initialized = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return false;
    IWICImagingFactory* factory = NULL;
    IWICStream* stream = NULL;
    IWICBitmapDecoder* decoder = NULL;
    IWICBitmapFrameDecode* frame = NULL;
    IWICFormatConverter* converter = NULL;
    uint8_t* pixels = NULL; size_t bytes = 0; UINT width = 0, height = 0;
    bool success = false;
    HRESULT hr = CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
        &IID_IWICImagingFactory, (void**)&factory);
    if (FAILED(hr)) goto done;
    hr = IWICImagingFactory_CreateStream(factory, &stream);
    if (FAILED(hr)) goto done;
    hr = IWICStream_InitializeFromMemory(stream, (BYTE*)data, (DWORD)length);
    if (FAILED(hr)) goto done;
    hr = IWICImagingFactory_CreateDecoder(factory, &GUID_ContainerFormatPng, NULL, &decoder);
    if (FAILED(hr)) goto done;
    hr = IWICBitmapDecoder_Initialize(decoder, (IStream*)stream, WICDecodeMetadataCacheOnDemand);
    if (FAILED(hr)) goto done;
    hr = IWICBitmapDecoder_GetFrame(decoder, 0, &frame);
    if (FAILED(hr)) goto done;
    hr = IWICBitmapFrameDecode_GetSize(frame, &width, &height);
    if (FAILED(hr) || !width || !height || (uint64_t)width * height > 16u * 1024u * 1024u) goto done;
    bytes = (size_t)width * height * 4;
    hr = IWICImagingFactory_CreateFormatConverter(factory, &converter);
    if (FAILED(hr)) goto done;
    hr = IWICFormatConverter_Initialize(converter, (IWICBitmapSource*)frame,
        &GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom);
    if (FAILED(hr)) goto done;
    pixels = ghostty_alloc(allocator, bytes);
    if (!pixels) goto done;
    hr = IWICFormatConverter_CopyPixels(converter, NULL, width * 4, (UINT)bytes, pixels);
    if (FAILED(hr)) goto done;
    *out = (GhosttySysImage){ .width = width, .height = height, .data = pixels, .data_len = bytes };
    success = true;
done:
    if (!success && pixels) ghostty_free(allocator, pixels, bytes);
    if (converter) IWICFormatConverter_Release(converter);
    if (frame) IWICBitmapFrameDecode_Release(frame);
    if (decoder) IWICBitmapDecoder_Release(decoder);
    if (stream) IWICStream_Release(stream);
    if (factory) IWICImagingFactory_Release(factory);
    if (SUCCEEDED(initialized)) CoUninitialize();
    return success;
}
static INIT_ONCE system_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK initialize_system(PINIT_ONCE once, PVOID parameter, PVOID* context) {
    (void)once; (void)parameter; (void)context;
    ghostty_sys_set(GHOSTTY_SYS_OPT_DECODE_PNG, (const void*)decode_png);
    return TRUE;
}

#define TRY(expr) do { int result_ = (expr); if (result_ != GHOSTTY_SUCCESS) return result_; } while (0)
typedef struct { GhosttyTrackedGridRef refs[4]; int32_t exit_code, has_exit_code; } TrackedMark;
typedef struct LinkEntry { struct LinkEntry* next; size_t index, length; uint8_t uri[]; } LinkEntry;
typedef struct AgwAnchor { struct AgwAnchor* next; AgwVt* owner; GhosttyTrackedGridRef ref; } AgwAnchor;
typedef struct AgwSearch { struct AgwSearch* next; AgwVt* owner; GhosttySearch search; } AgwSearch;
extern int agwinterm_ghostty_search_unicode(GhosttySearch, const uint8_t*, size_t);
extern int agwinterm_ghostty_search_tick(GhosttySearch, uint32_t*);
extern int agwinterm_ghostty_search_matches(GhosttySearch, size_t, GhosttySelection*, size_t, size_t*);
typedef struct ImagePixels {
    struct ImagePixels* next;
    _Atomic uint32_t references;
    uint64_t generation;
    uint32_t width, height, format;
    size_t length;
    uint8_t data[];
} ImagePixels;
typedef struct ImageEntry { struct ImageEntry* next; ImagePixels* pixels; AgwVtImage value; } ImageEntry;
struct AgwVt {
    GhosttyTerminal terminal;
    GhosttyRenderState render;
    GhosttyRenderState held, capture;
    LinkEntry *render_links, *held_links, *capture_links;
    ImageEntry *render_images, *held_images, *capture_images;
    ImagePixels* image_cache;
    GhosttyRenderStateImageIterator images;
    GhosttyKeyEncoder key_encoder;
    GhosttyKeyEvent key_event;
    GhosttyMouseEncoder mouse_encoder;
    GhosttyMouseEvent mouse_event;
    GhosttySearch search;
    uint64_t held_since, held_serial, capture_serial, last_serial;
    int holding, render_invalidated, capture_busy, last_valid;
    SRWLOCK capture_guard;
    SRWLOCK anchor_guard;
    AgwAnchor* released_anchors;
    AgwSearch* released_searches;
    GhosttyRenderStateRowIterator rows;
    GhosttyRenderStateRowCells cells;
    AgwVtEvent event;
    void* context;
    int updating;
    TrackedMark marks[512];
    size_t mark_count;
    GhosttyTrackedGridRef scroll_probe;
    uint64_t scroll_epoch;
    int effect_error;
    GhosttyAllocator allocator;
    _Atomic size_t allocated;
};
// The same allocator lives through decoder/terminal/render-state ownership.
// Account before allocating so malformed snapshots cannot request unbounded memory.
static void* budget_alloc(void* context, size_t length, uint8_t alignment, uintptr_t return_address) {
    (void)return_address; AgwVt* vt = context;
    const size_t limit = 256u * 1024u * 1024u;
    if (length > limit || alignment > 24) return NULL;
    size_t current = atomic_load(&vt->allocated);
    do { if (current > limit - length) return NULL; }
    while (!atomic_compare_exchange_weak(&vt->allocated, &current, current + length));
    size_t align = (size_t)1 << alignment;
    if (align < sizeof(void*)) align = sizeof(void*);
    void* memory = _aligned_malloc(length, align);
    if (!memory) atomic_fetch_sub(&vt->allocated, length);
    return memory;
}
static void budget_free(void* context, void* memory, size_t length, uint8_t alignment, uintptr_t return_address) {
    (void)alignment; (void)return_address; AgwVt* vt = context;
    _aligned_free(memory); atomic_fetch_sub(&vt->allocated, length);
}
static const GhosttyAllocatorVtable budget_vtable = { .alloc = budget_alloc, .free = budget_free };
static void free_images(AgwVt* vt, ImageEntry* images) {
    while (images) {
        ImageEntry* next = images->next;
        atomic_fetch_sub(&images->pixels->references, 1);
        budget_free(vt, images, sizeof(*images), 0, 0); images = next;
    }
}
// The cache owns one reference. Readers only release snapshot references;
// reclamation happens on the serialized terminal thread, never during a copy.
static void prune_images(AgwVt* vt) {
    ImagePixels** link = &vt->image_cache;
    while (*link) {
        ImagePixels* pixels = *link;
        if (atomic_load(&pixels->references) == 1) {
            *link = pixels->next;
            budget_free(vt, pixels, sizeof(*pixels) + pixels->length, 0, 0);
        } else link = &pixels->next;
    }
}
static int capture_images(AgwVt* vt, GhosttyRenderState state, ImageEntry** out) {
    *out = NULL; ImageEntry** tail = out;
    prune_images(vt);
    TRY(ghostty_render_state_get(state, GHOSTTY_RENDER_STATE_DATA_IMAGE_ITERATOR, &vt->images));
    GhosttyKittyGraphics graphics;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_KITTY_GRAPHICS, &graphics));
    int result = 0; size_t count = 0;
    while (!result && ghostty_render_state_image_iterator_next(vt->images)) {
        AgwVtImage value = {0}; GhosttyImageProtocol protocol;
        GhosttyRenderStateImageData keys[] = {
            GHOSTTY_RENDER_STATE_IMAGE_DATA_PROTOCOL, GHOSTTY_RENDER_STATE_IMAGE_DATA_IMAGE_ID,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_IMAGE_GENERATION, GHOSTTY_RENDER_STATE_IMAGE_DATA_LAYER,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_Z, GHOSTTY_RENDER_STATE_IMAGE_DATA_VIEWPORT_COL,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_VIEWPORT_ROW, GHOSTTY_RENDER_STATE_IMAGE_DATA_CELL_OFFSET_X,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_CELL_OFFSET_Y, GHOSTTY_RENDER_STATE_IMAGE_DATA_PIXEL_WIDTH,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_PIXEL_HEIGHT, GHOSTTY_RENDER_STATE_IMAGE_DATA_SOURCE_X,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_SOURCE_Y, GHOSTTY_RENDER_STATE_IMAGE_DATA_SOURCE_WIDTH,
            GHOSTTY_RENDER_STATE_IMAGE_DATA_SOURCE_HEIGHT };
        void* values[] = { &protocol, &value.id, &value.generation, &value.layer, &value.z, &value.col,
            &value.row, &value.offset_x, &value.offset_y, &value.width, &value.height,
            &value.source_x, &value.source_y, &value.source_width, &value.source_height };
        result = ghostty_render_state_image_get_multi(vt->images, 15, keys, values, NULL);
        if (result) break;
        if (protocol != GHOSTTY_IMAGE_PROTOCOL_KITTY) continue;
        if (++count > 65536) { result = GHOSTTY_LIMIT_EXCEEDED; break; }
        ImagePixels* pixels = vt->image_cache;
        while (pixels && pixels->generation != value.generation) pixels = pixels->next;
        if (!pixels) {
            GhosttyKittyGraphicsImage image = ghostty_kitty_graphics_image(graphics, value.id);
            if (!image) continue;
            const uint8_t* data; size_t length; uint64_t generation;
            GhosttyKittyGraphicsImageData image_keys[] = { GHOSTTY_KITTY_IMAGE_DATA_WIDTH,
                GHOSTTY_KITTY_IMAGE_DATA_HEIGHT, GHOSTTY_KITTY_IMAGE_DATA_FORMAT,
                GHOSTTY_KITTY_IMAGE_DATA_DATA_PTR, GHOSTTY_KITTY_IMAGE_DATA_DATA_LEN,
                GHOSTTY_KITTY_IMAGE_DATA_GENERATION };
            void* image_values[] = { &value.image_width, &value.image_height, &value.format, &data, &length, &generation };
            result = ghostty_kitty_graphics_image_get_multi(image, 6, image_keys, image_values, NULL);
            if (result == GHOSTTY_NO_VALUE) { result = 0; continue; }
            if (result) break;
            if (generation != value.generation || length > 64u * 1024u * 1024u) { result = GHOSTTY_INVALID_VALUE; break; }
            pixels = budget_alloc(vt, sizeof(*pixels) + length, 0, 0);
            if (!pixels) { result = GHOSTTY_OUT_OF_MEMORY; break; }
            pixels->next = vt->image_cache; pixels->generation = generation;
            atomic_init(&pixels->references, 1);
            pixels->width = value.image_width; pixels->height = value.image_height; pixels->format = value.format;
            pixels->length = length; memcpy(pixels->data, data, length); vt->image_cache = pixels;
        }
        ImageEntry* entry = budget_alloc(vt, sizeof(*entry), 0, 0);
        if (!entry) { result = GHOSTTY_OUT_OF_MEMORY; break; }
        value.image_width = pixels->width; value.image_height = pixels->height;
        value.format = pixels->format; value.data_length = (uint32_t)pixels->length;
        atomic_fetch_add(&pixels->references, 1);
        *entry = (ImageEntry){ .next = NULL, .pixels = pixels, .value = value };
        *tail = entry; tail = &entry->next;
    }
    if (result) { free_images(vt, *out); *out = NULL; }
    return result;
}
static void flush_anchors(AgwVt* vt) {
    AcquireSRWLockExclusive(&vt->anchor_guard);
    AgwAnchor* anchor = vt->released_anchors; vt->released_anchors = NULL;
    AgwSearch* search = vt->released_searches; vt->released_searches = NULL;
    ReleaseSRWLockExclusive(&vt->anchor_guard);
    while (anchor) {
        AgwAnchor* next = anchor->next;
        ghostty_tracked_grid_ref_free(anchor->ref);
        budget_free(vt, anchor, sizeof(*anchor), 0, 0); anchor = next;
    }
    while (search) {
        AgwSearch* next = search->next;
        ghostty_search_free(search->search);
        budget_free(vt, search, sizeof(*search), 0, 0); search = next;
    }
}
static void free_links(AgwVt* vt, LinkEntry* links) {
    while (links) { LinkEntry* next = links->next; budget_free(vt, links, sizeof(*links) + links->length, 0, 0); links = next; }
}
// The public render API retains cells but does not expose hyperlink URIs.
// Copy only linked cells while the terminal is locked. The resulting list is
// owned by that render snapshot, including pre-hold and in-flight frames.
static int capture_links(AgwVt* vt, LinkEntry** out) {
    *out = NULL; LinkEntry** tail = out;
    uint16_t cols, rows; size_t total = 0;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &cols));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows));
    int result = 0;
    for (uint16_t y = 0; !result && y < rows; y++) {
        GhosttyPoint point = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = 0, .y = y } } };
        GhosttyGridRef ref = {0};
        GhosttyRow row; bool linked;
        result = ghostty_terminal_grid_ref(vt->terminal, point, &ref);
        if (!result) result = ghostty_grid_ref_row(&ref, &row);
        if (!result) result = ghostty_row_get(row, GHOSTTY_ROW_DATA_HYPERLINK, &linked);
        if (result || !linked) continue;
        for (uint16_t x = 0; !result && x < cols; x++) {
            point.value.coordinate.x = x;
            GhosttyCell cell;
            result = ghostty_terminal_grid_ref(vt->terminal, point, &ref);
            if (!result) result = ghostty_grid_ref_cell(&ref, &cell);
            if (!result) result = ghostty_cell_get(cell, GHOSTTY_CELL_DATA_HAS_HYPERLINK, &linked);
            if (result || !linked) continue;
            size_t length;
            result = ghostty_grid_ref_hyperlink_uri(&ref, NULL, 0, &length);
            if (result != GHOSTTY_OUT_OF_SPACE && result != GHOSTTY_SUCCESS) break;
            result = 0;
            if (length > 16u * 1024u * 1024u - total) { result = GHOSTTY_LIMIT_EXCEEDED; break; }
            LinkEntry* entry = budget_alloc(vt, sizeof(LinkEntry) + length, 0, 0);
            if (!entry) { result = GHOSTTY_OUT_OF_MEMORY; break; }
            entry->next = NULL; entry->index = (size_t)y * cols + x; entry->length = length;
            *tail = entry; tail = &entry->next; total += length;
            result = ghostty_grid_ref_hyperlink_uri(&ref, entry->uri, length, &length);
        }
    }
    if (result) { free_links(vt, *out); *out = NULL; }
    return result;
}

uint32_t agwvt_abi(uint32_t* cell_size, uint32_t* info_size) {
    if (cell_size) *cell_size = sizeof(AgwVtCell);
    if (info_size) *info_size = sizeof(AgwVtInfo);
    return 3;
}
const char* agwvt_revision(void) { return "f5a1a0fedd8762d490bc2951dc4b63a0f1b030e0"; }
static void reply(GhosttyTerminal terminal, void* context, const uint8_t* data, size_t len) {
    (void)terminal;
    AgwVt* vt = context;
    if (vt->event) vt->event(vt->context, 1, data, len);
}
static void bell(GhosttyTerminal terminal, void* context) {
    (void)terminal;
    AgwVt* vt = context;
    if (vt->event) vt->event(vt->context, 2, NULL, 0);
}
static void title_changed(GhosttyTerminal terminal, void* context) {
    (void)terminal; AgwVt* vt = context;
    if (vt->event) vt->event(vt->context, 3, NULL, 0);
}
static void pwd_changed(GhosttyTerminal terminal, void* context) {
    (void)terminal; AgwVt* vt = context;
    if (vt->event) vt->event(vt->context, 4, NULL, 0);
}
static void notification(GhosttyTerminal terminal, void* context, const GhosttyTerminalDesktopNotification* value) {
    (void)terminal; AgwVt* vt = context;
    if (!vt->event) return;
    vt->event(vt->context, 5, value->title.ptr, value->title.len);
    vt->event(vt->context, 6, value->body.ptr, value->body.len);
}
static void progress(GhosttyTerminal terminal, void* context, const GhosttyTerminalProgressReport* value) {
    (void)terminal; AgwVt* vt = context;
    int32_t data[2] = { value->state, value->progress };
    if (vt->event) vt->event(vt->context, 7, (const uint8_t*)data, sizeof(data));
}
static void unknown(GhosttyTerminal terminal, void* context, const GhosttyTerminalUnknownSequence* value) {
    (void)terminal; AgwVt* vt = context;
    if (!vt->event) return;
    if (value->tag == GHOSTTY_TERMINAL_UNKNOWN_SEQUENCE_OSC && !value->value.osc.truncated)
        vt->event(vt->context, 8, value->value.osc.content.ptr, value->value.osc.content.len);
}
static void clipboard(GhosttyTerminal terminal, void* context, const GhosttyClipboardWrite* value) {
    (void)terminal; AgwVt* vt = context;
    // Legacy OSC 52 text writes use the existing write-only host policy.
    // Multi-representation writes need an atomic host API and are not acknowledged here.
    if (vt->event && value->contents_len == 1) {
        const GhosttyClipboardContent* content = &value->contents[0];
        if (content->mime.len >= 10 && memcmp(content->mime.ptr, "text/plain", 10) == 0 && content->data.len <= 1024 * 1024)
            vt->event(vt->context, 9, content->data.ptr, content->data.len);
    }
}
static int32_t mark_line(GhosttyTrackedGridRef ref) {
    if (!ref) return -1;
    GhosttyPointCoordinate point;
    return ghostty_tracked_grid_ref_point(ref, GHOSTTY_POINT_TAG_SCREEN, &point) == GHOSTTY_SUCCESS ? (int32_t)point.y : -1;
}
static void free_mark(TrackedMark* mark) {
    for (size_t i = 0; i < 4; i++) ghostty_tracked_grid_ref_free(mark->refs[i]);
}
static void semantic_prompt(GhosttyTerminal terminal, void* context, const GhosttyTerminalSemanticPrompt* event) {
    AgwVt* vt = context; GhosttyTerminalScreen screen;
    if (ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &screen) || screen != GHOSTTY_TERMINAL_SCREEN_PRIMARY) return;
    if (event->kind < 1 || event->kind > 4 || (event->kind == 1 && event->prompt_kind != GHOSTTY_SEMANTIC_PROMPT_PROMPT_PRIMARY)) return;
    uint16_t x, y; size_t history;
    if (ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_CURSOR_X, &x) ||
        ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_CURSOR_Y, &y) ||
        ghostty_terminal_get(terminal, GHOSTTY_TERMINAL_DATA_SCROLLBACK_ROWS, &history)) return;
    if (event->kind == 1) {
        if (vt->mark_count && mark_line(vt->marks[vt->mark_count - 1].refs[0]) == (int32_t)(history + y)) return;
        if (vt->mark_count == 512) { free_mark(&vt->marks[0]); memmove(vt->marks, vt->marks + 1, 511 * sizeof(TrackedMark)); vt->mark_count--; }
        memset(&vt->marks[vt->mark_count++], 0, sizeof(TrackedMark));
    }
    if (!vt->mark_count) return;
    TrackedMark* mark = &vt->marks[vt->mark_count - 1];
    size_t index = event->kind - 1;
    if (!mark->refs[index]) {
        GhosttyPoint point = { .tag = GHOSTTY_POINT_TAG_ACTIVE, .value = { .coordinate = { .x = x, .y = y } } };
        int result = ghostty_terminal_grid_ref_track(terminal, point, &mark->refs[index]);
        if (result) vt->effect_error = result;
    }
    if (event->kind == 4) { mark->exit_code = event->exit_code; mark->has_exit_code = event->has_exit_code; }
}
static void render_hold(GhosttyTerminal terminal, void* context, bool held) {
    AgwVt* vt = context;
    if (!held) {
        AcquireSRWLockExclusive(&vt->capture_guard); vt->holding = 0; ReleaseSRWLockExclusive(&vt->capture_guard);
        uint64_t deadline = 0;
        if (vt->event) vt->event(vt->context, 10, (const uint8_t*)&deadline, sizeof(deadline));
        return;
    }
    // A separate state protects an in-flight reader. Never mutate a state being
    // consumed outside the session lock, even if another hold begins meanwhile.
    GhosttyRenderState next = NULL;
    LinkEntry* links = NULL;
    ImageEntry* images = NULL;
    int result = capture_links(vt, &links);
    if (!result) result = ghostty_render_state_new(&vt->allocator, &next);
    if (!result) result = ghostty_render_state_update(next, terminal);
    if (!result) result = capture_images(vt, next, &images);
    if (result) { free_links(vt, links); ghostty_render_state_free(next); vt->effect_error = result; return; }
    AcquireSRWLockExclusive(&vt->capture_guard);
    vt->holding = 1;
    if (vt->held && vt->held != vt->capture) { ghostty_render_state_free(vt->held); free_links(vt, vt->held_links); free_images(vt, vt->held_images); }
    vt->held_images = images;
    vt->held_links = links;
    vt->held = next; vt->held_since = GetTickCount64(); vt->held_serial++;
    vt->render_invalidated = 1; // The callback consumed terminal dirty flags.
    ReleaseSRWLockExclusive(&vt->capture_guard);
    uint64_t deadline = vt->held_since + 1000;
    if (vt->event) vt->event(vt->context, 10, (const uint8_t*)&deadline, sizeof(deadline));
}
int agwvt_marks(AgwVt* vt, AgwVtMark* marks, size_t capacity, size_t* count) {
    if (!vt || !marks || !count) return GHOSTTY_INVALID_VALUE;
    *count = 0;
    for (size_t i = 0; i < vt->mark_count; i++) {
        TrackedMark* mark = &vt->marks[i];
        int32_t prompt = mark_line(mark->refs[0]);
        if (prompt < 0) continue;
        if (*count >= capacity) return GHOSTTY_OUT_OF_SPACE;
        marks[(*count)++] = (AgwVtMark){ prompt, mark_line(mark->refs[1]), mark_line(mark->refs[2]), mark_line(mark->refs[3]), mark->exit_code, mark->has_exit_code };
    }
    return GHOSTTY_SUCCESS;
}
int agwvt_anchor_new(AgwVt* vt, uint32_t col, uint32_t row, void** anchor) {
    if (!vt || !anchor) return GHOSTTY_INVALID_VALUE;
    flush_anchors(vt);
    GhosttyPoint point = { .tag = GHOSTTY_POINT_TAG_SCREEN, .value = { .coordinate = { .x = col, .y = row } } };
    *anchor = NULL;
    AgwAnchor* owned = budget_alloc(vt, sizeof(*owned), 0, 0);
    if (!owned) return GHOSTTY_OUT_OF_MEMORY;
    owned->next = NULL; owned->owner = vt; owned->ref = NULL;
    int result = ghostty_terminal_grid_ref_track(vt->terminal, point, &owned->ref);
    if (result) { budget_free(vt, owned, sizeof(*owned), 0, 0); return result; }
    *anchor = owned; return GHOSTTY_SUCCESS;
}
int agwvt_anchor_point(void* anchor, uint32_t* col, uint32_t* row) {
    if (!anchor || !col || !row) return GHOSTTY_INVALID_VALUE;
    GhosttyPointCoordinate point;
    TRY(ghostty_tracked_grid_ref_point(((AgwAnchor*)anchor)->ref, GHOSTTY_POINT_TAG_SCREEN, &point));
    *col = point.x; *row = point.y; return GHOSTTY_SUCCESS;
}
void agwvt_anchor_free(void* pointer) {
    if (!pointer) return;
    // SafeHandle finalizers must not acquire a managed session lock or mutate
    // Ghostty's pin tables concurrently with Feed. Enqueue without allocating;
    // the next serialized terminal operation (or terminal destruction) frees it.
    AgwAnchor* anchor = pointer; AgwVt* vt = anchor->owner;
    AcquireSRWLockExclusive(&vt->anchor_guard);
    anchor->next = vt->released_anchors; vt->released_anchors = anchor;
    ReleaseSRWLockExclusive(&vt->anchor_guard);
}
void agwvt_free(AgwVt* vt) {
    if (!vt) return;
    flush_anchors(vt);
    for (size_t i = 0; i < vt->mark_count; i++) free_mark(&vt->marks[i]);
    ghostty_tracked_grid_ref_free(vt->scroll_probe);
    ghostty_render_state_row_cells_free(vt->cells);
    ghostty_render_state_row_iterator_free(vt->rows);
    if (vt->capture && vt->capture != vt->held && vt->capture != vt->render) { ghostty_render_state_free(vt->capture); free_links(vt, vt->capture_links); free_images(vt, vt->capture_images); }
    free_images(vt, vt->held_images); free_images(vt, vt->render_images); prune_images(vt);
    ghostty_render_state_image_iterator_free(vt->images);
    ghostty_key_event_free(vt->key_event);
    ghostty_key_encoder_free(vt->key_encoder);
    ghostty_mouse_event_free(vt->mouse_event);
    ghostty_mouse_encoder_free(vt->mouse_encoder);
    ghostty_search_free(vt->search);
    free_links(vt, vt->held_links); free_links(vt, vt->render_links);
    ghostty_render_state_free(vt->held);
    ghostty_render_state_free(vt->render);
    ghostty_terminal_free(vt->terminal);
    free(vt);
}
static int configure_terminal(AgwVt* vt, int initialize_colors) {
    int result = GHOSTTY_SUCCESS;
    GhosttyColorRgb foreground = {229, 229, 229}, background = {0, 0, 0};
    if (initialize_colors && !result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground);
    if (initialize_colors && !result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_USERDATA, vt);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY, (const void*)reply);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_BELL, (const void*)bell);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_TITLE_CHANGED, (const void*)title_changed);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_PWD_CHANGED, (const void*)pwd_changed);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION, (const void*)notification);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_PROGRESS_REPORT, (const void*)progress);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_UNKNOWN_SEQUENCE, (const void*)unknown);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE, (const void*)clipboard);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT, (const void*)semantic_prompt);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_RENDER_HOLD, (const void*)render_hold);
    size_t unknown_limit = 4096;
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_UNKNOWN_MAX_BYTES, &unknown_limit);
    size_t continuation_limit = 64 * 1024;
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_CONTINUATION_MAX_BYTES, &continuation_limit);
    size_t history_bytes = 64u * 1024u * 1024u;
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_BYTES, &history_bytes);
    bool disabled = false;
    GhosttyString no_temp_directory = { .ptr = (const uint8_t*)"", .len = 0 };
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_RESIZE_PULL_SCROLLBACK, &disabled);
    // Only direct graphics payloads are allowed; terminal output cannot read host files/shared memory.
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_FILE, &disabled);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_TEMP_FILE, &no_temp_directory);
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_SHARED_MEM, &disabled);
    uint64_t image_limit = 64u * 1024u * 1024u;
    if (!result) result = ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_STORAGE_LIMIT, &image_limit);
    return result;
}
int agwvt_new(uint32_t cols, uint32_t rows, AgwVtEvent event, void* context, AgwVt** out) {
    if (!out || !cols || !rows || cols > 32768 || rows > 32768 || (uint64_t)cols * rows > 4000000)
        return GHOSTTY_INVALID_VALUE;
    *out = NULL;
    InitOnceExecuteOnce(&system_once, initialize_system, NULL, NULL);
    AgwVt* vt = calloc(1, sizeof(*vt));
    if (!vt) return GHOSTTY_OUT_OF_MEMORY;
    vt->event = event; vt->context = context;
    vt->allocator = (GhosttyAllocator){ .ctx = vt, .vtable = &budget_vtable };
    int result = ghostty_terminal_new(&vt->allocator, &vt->terminal, (uint16_t)cols, (uint16_t)rows);
    if (!result) result = ghostty_render_state_new(&vt->allocator, &vt->render);
    if (!result) result = ghostty_render_state_row_iterator_new(&vt->allocator, &vt->rows);
    if (!result) result = ghostty_render_state_row_cells_new(&vt->allocator, &vt->cells);
    if (!result) result = ghostty_render_state_image_iterator_new(&vt->allocator, &vt->images);
    if (!result) result = ghostty_key_encoder_new(&vt->allocator, &vt->key_encoder);
    if (!result) result = ghostty_key_event_new(&vt->allocator, &vt->key_event);
    if (!result) result = ghostty_mouse_encoder_new(&vt->allocator, &vt->mouse_encoder);
    if (!result) result = ghostty_mouse_event_new(&vt->allocator, &vt->mouse_event);
    if (!result) result = configure_terminal(vt, 1);
    GhosttyPoint top = { .tag = GHOSTTY_POINT_TAG_ACTIVE, .value = { .coordinate = { .x = 0, .y = 0 } } };
    if (!result) result = ghostty_terminal_grid_ref_track(vt->terminal, top, &vt->scroll_probe);
    if (result) { agwvt_free(vt); return result; }
    *out = vt;
    return GHOSTTY_SUCCESS;
}
int agwvt_snapshot(AgwVt* vt, uint8_t** bytes, size_t* length) {
    if (!vt || !bytes || !length || vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    *bytes = NULL; *length = 0;
    TRY(ghostty_snapshot_encode_alloc(vt->terminal, &vt->allocator, bytes, length));
    if (*length > 64u * 1024u * 1024u) {
        ghostty_free(&vt->allocator, *bytes, *length); *bytes = NULL; *length = 0;
        return GHOSTTY_LIMIT_EXCEEDED;
    }
    // Tracked references are application ownership, not part of Ghostty's
    // terminal snapshot. Persist their coordinates beside the opaque payload.
    size_t header = 12 + vt->mark_count * 40;
    if (*length > 64u * 1024u * 1024u - header) {
        ghostty_free(&vt->allocator, *bytes, *length); *bytes = NULL; *length = 0;
        return GHOSTTY_LIMIT_EXCEEDED;
    }
    uint8_t* wrapped = budget_alloc(vt, header + *length, 0, 0);
    if (!wrapped) { ghostty_free(&vt->allocator, *bytes, *length); *bytes = NULL; *length = 0; return GHOSTTY_OUT_OF_MEMORY; }
    memcpy(wrapped, "AGMK0001", 8);
    uint32_t count = (uint32_t)vt->mark_count; memcpy(wrapped + 8, &count, 4);
    for (size_t i = 0; i < vt->mark_count; i++) {
        uint32_t data[10];
        for (size_t j = 0; j < 4; j++) {
            GhosttyPointCoordinate point;
            int found = vt->marks[i].refs[j] && ghostty_tracked_grid_ref_point(vt->marks[i].refs[j], GHOSTTY_POINT_TAG_SCREEN, &point) == GHOSTTY_SUCCESS;
            data[j * 2] = found ? point.x : UINT32_MAX;
            data[j * 2 + 1] = found ? point.y : UINT32_MAX;
        }
        data[8] = (uint32_t)vt->marks[i].exit_code; data[9] = (uint32_t)vt->marks[i].has_exit_code;
        memcpy(wrapped + 12 + i * 40, data, 40);
    }
    memcpy(wrapped + header, *bytes, *length);
    ghostty_free(&vt->allocator, *bytes, *length);
    *bytes = wrapped; *length += header;
    return GHOSTTY_SUCCESS;
}
void agwvt_snapshot_free(AgwVt* vt, uint8_t* bytes, size_t length) { if (vt) ghostty_free(&vt->allocator, bytes, length); }
int agwvt_restore(AgwVt* vt, const uint8_t* bytes, size_t length) {
    if (!vt || !bytes || !length || length > 64u * 1024u * 1024u || vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    flush_anchors(vt);
    if (length < 12 || memcmp(bytes, "AGMK0001", 8)) return GHOSTTY_INVALID_VALUE;
    uint32_t mark_count; memcpy(&mark_count, bytes + 8, 4);
    size_t header = 12 + (size_t)mark_count * 40;
    if (mark_count > 512 || header >= length) return GHOSTTY_INVALID_VALUE;
    const uint8_t* mark_bytes = bytes + 12;
    bytes += header; length -= header;
    GhosttySnapshotDecoder decoder = NULL;
    TRY(ghostty_snapshot_decoder_new_buf(&vt->allocator, &decoder, bytes, length));
    size_t continuation_limit = 64 * 1024; bool retain = true;
    int result = ghostty_snapshot_decoder_set(decoder, GHOSTTY_SNAPSHOT_DECODER_OPT_MAX_CONTINUATION_BYTES, &continuation_limit);
    if (!result) result = ghostty_snapshot_decoder_set(decoder, GHOSTTY_SNAPSHOT_DECODER_OPT_RETAIN_CONTINUATION, &retain);
    uint64_t image_limit = 64u * 1024u * 1024u;
    if (!result) result = ghostty_snapshot_decoder_set(decoder, GHOSTTY_SNAPSHOT_DECODER_OPT_KITTY_IMAGE_STORAGE_LIMIT, &image_limit);
    GhosttyTerminal replacement = NULL;
    if (!result) result = ghostty_snapshot_decoder_decode(decoder, &replacement);
    if (!result) {
        size_t consumed;
        result = ghostty_snapshot_decoder_get(decoder, GHOSTTY_SNAPSHOT_DECODER_DATA_SOURCE_OFFSET, &consumed);
        if (!result && consumed != length) result = GHOSTTY_INVALID_VALUE;
    }
    ghostty_snapshot_decoder_free(decoder);
    if (result) { ghostty_terminal_free(replacement); return result; }
    uint16_t cols, rows;
    result = ghostty_terminal_get(replacement, GHOSTTY_TERMINAL_DATA_COLS, &cols);
    if (!result) result = ghostty_terminal_get(replacement, GHOSTTY_TERMINAL_DATA_ROWS, &rows);
    if (!result && (!cols || !rows || cols > 32768 || rows > 32768 || (uint64_t)cols * rows > 4000000)) result = GHOSTTY_LIMIT_EXCEEDED;
    GhosttyRenderState render = NULL;
    if (!result) result = ghostty_render_state_new(&vt->allocator, &render);
    if (result) { ghostty_terminal_free(replacement); return result; }
    GhosttyTrackedGridRef probe = NULL;
    GhosttyPoint top = { .tag = GHOSTTY_POINT_TAG_ACTIVE, .value = { .coordinate = { .x = 0, .y = 0 } } };
    result = ghostty_terminal_grid_ref_track(replacement, top, &probe);
    TrackedMark restored_marks[512] = {0};
    for (size_t i = 0; !result && i < mark_count; i++) {
        uint32_t data[10]; memcpy(data, mark_bytes + i * 40, 40);
        if (data[9] > 1) { result = GHOSTTY_INVALID_VALUE; break; }
        restored_marks[i].exit_code = (int32_t)data[8]; restored_marks[i].has_exit_code = (int32_t)data[9];
        for (size_t j = 0; !result && j < 4; j++) {
            if (data[j * 2] == UINT32_MAX && data[j * 2 + 1] == UINT32_MAX) continue;
            GhosttyPoint point = { .tag = GHOSTTY_POINT_TAG_SCREEN,
                .value = { .coordinate = { .x = data[j * 2], .y = data[j * 2 + 1] } } };
            result = ghostty_terminal_grid_ref_track(replacement, point, &restored_marks[i].refs[j]);
        }
    }
    GhosttyTerminal original = vt->terminal; vt->terminal = replacement;
    if (!result) result = configure_terminal(vt, 0);
    if (result) {
        vt->terminal = original;
        for (size_t i = 0; i < mark_count; i++) free_mark(&restored_marks[i]);
        ghostty_tracked_grid_ref_free(probe);
        ghostty_terminal_free(replacement); ghostty_render_state_free(render); return result;
    }
    ghostty_search_free(vt->search); vt->search = NULL;
    for (size_t i = 0; i < vt->mark_count; i++) free_mark(&vt->marks[i]);
    ghostty_tracked_grid_ref_free(vt->scroll_probe); vt->scroll_probe = probe; vt->scroll_epoch++;
    memcpy(vt->marks, restored_marks, sizeof(restored_marks)); vt->mark_count = mark_count;
    ghostty_terminal_free(original); ghostty_render_state_free(vt->render); vt->render = render;
    ghostty_render_state_free(vt->held); vt->held = NULL; vt->capture = NULL;
    free_links(vt, vt->held_links); free_links(vt, vt->render_links);
    vt->held_links = vt->render_links = vt->capture_links = NULL;
    free_images(vt, vt->held_images); free_images(vt, vt->render_images); prune_images(vt);
    vt->held_images = vt->render_images = vt->capture_images = NULL;
    vt->holding = 0; vt->render_invalidated = 0; vt->last_valid = 0;
    vt->effect_error = 0;
    return GHOSTTY_SUCCESS;
}
int agwvt_feed(AgwVt* vt, const uint8_t* bytes, size_t len) {
    if (!vt || (!bytes && len)) return GHOSTTY_INVALID_VALUE;
    flush_anchors(vt);
    GhosttyTerminalScreen before;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &before));
    ghostty_terminal_vt_write(vt->terminal, bytes, len);
    if (before == GHOSTTY_TERMINAL_SCREEN_PRIMARY) {
        GhosttyPointCoordinate point;
        int located = ghostty_tracked_grid_ref_point(vt->scroll_probe, GHOSTTY_POINT_TAG_ACTIVE, &point);
        if (located == GHOSTTY_NO_VALUE || (!located && point.y != 0)) vt->scroll_epoch++;
        else if (located) return located;
    }
    GhosttyPoint top = { .tag = GHOSTTY_POINT_TAG_ACTIVE, .value = { .coordinate = { .x = 0, .y = 0 } } };
    TRY(ghostty_tracked_grid_ref_set(vt->scroll_probe, vt->terminal, top));
    if (vt->effect_error) return vt->effect_error;
    bool error = false;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_VT_PROCESSING_ERROR, &error));
    return error ? GHOSTTY_INVALID_VALUE : GHOSTTY_SUCCESS;
}
int agwvt_resize(AgwVt* vt, uint32_t cols, uint32_t rows, uint32_t cw, uint32_t ch) {
    if (!vt || !cols || !rows || cols > 32768 || rows > 32768 || (uint64_t)cols * rows > 4000000)
        return GHOSTTY_INVALID_VALUE;
    flush_anchors(vt);
    TRY(ghostty_terminal_resize(vt->terminal, (uint16_t)cols, (uint16_t)rows, cw, ch));
    GhosttyPoint top = { .tag = GHOSTTY_POINT_TAG_ACTIVE, .value = { .coordinate = { .x = 0, .y = 0 } } };
    return ghostty_tracked_grid_ref_set(vt->scroll_probe, vt->terminal, top);
}
static uint32_t rgb(GhosttyColorRgb value) { return ((uint32_t)value.r << 16) | ((uint32_t)value.g << 8) | value.b; }
extern int agwinterm_ghostty_metadata(GhosttyTerminal, uint32_t*, uint32_t*, uint32_t*, uint32_t*);
int agwvt_info(AgwVt* vt, AgwVtInfo* out) {
    if (!vt || !out) return GHOSTTY_INVALID_VALUE;
    memset(out, 0, sizeof(*out));
    uint16_t cols, rows, x, y; bool visible; uint8_t keyboard; size_t history;
    GhosttyTerminalScreen screen; GhosttyColorRgb bg, default_bg;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &cols));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_CURSOR_X, &x));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_CURSOR_Y, &y));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_CURSOR_VISIBLE, &visible));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &screen));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_SCROLLBACK_ROWS, &history));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_KITTY_KEYBOARD_FLAGS, &keyboard));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_BACKGROUND, &bg));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_BACKGROUND_DEFAULT, &default_bg));
    out->cols = cols; out->rows = rows; out->cursor_col = x; out->cursor_row = y;
    out->cursor_visible = visible; out->alt_screen = screen != 0; out->history = (uint32_t)history;
    out->keyboard_flags = keyboard; out->background = rgb(bg);
    out->default_background = rgb(default_bg);
    out->scroll_epoch = vt->scroll_epoch;
    TRY(agwinterm_ghostty_metadata(vt->terminal, &out->cursor_shape, &out->scroll_top, &out->scroll_bottom, &out->background_override));
    const uint16_t modes[] = {1000,1002,1003,1006,1016,2004,1004,2026,9};
    for (unsigned i = 0; i < sizeof(modes)/sizeof(modes[0]); i++) {
        GhosttyTerminalModeConfig mode = { .mode = ghostty_mode_new(modes[i], false) };
        TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode));
        if (mode.value) out->modes |= 1u << i;
    }
    return GHOSTTY_SUCCESS;
}
extern int agwinterm_ghostty_prepend_history(GhosttyTerminal destination, GhosttyTerminal source, uint32_t max_rows, size_t* added);
int agwvt_prepend_history(AgwVt* vt, uint32_t source_cols, const uint8_t* bytes, size_t length, uint32_t max_rows, size_t* added) {
    if (!vt || vt->capture_busy || !added || !source_cols || source_cols > 32768 ||
        length > 64u * 1024u * 1024u || (!bytes && length)) return GHOSTTY_INVALID_VALUE;
    *added = 0;
    GhosttyTerminal source = NULL;
    TRY(ghostty_terminal_new(&vt->allocator, &source, (uint16_t)source_cols, 1));
    size_t lines = max_rows;
    int result = ghostty_terminal_set(source, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, &lines);
    size_t history_bytes = 64u * 1024u * 1024u;
    if (!result) result = ghostty_terminal_set(source, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_BYTES, &history_bytes);
    if (!result) {
        ghostty_terminal_vt_write(source, bytes, length);
        bool failed = false;
        result = ghostty_terminal_get(source, GHOSTTY_TERMINAL_DATA_VT_PROCESSING_ERROR, &failed);
        if (!result && failed) result = GHOSTTY_OUT_OF_MEMORY;
    }
    uint16_t cols;
    if (!result) result = ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &cols);
    if (!result) result = ghostty_terminal_resize(source, cols, 1, 8, 18);
    if (!result) result = agwinterm_ghostty_prepend_history(vt->terminal, source, max_rows, added);
    ghostty_terminal_free(source);
    if (!result && *added) { vt->render_invalidated = 1; vt->scroll_epoch++; }
    return result;
}
int agwvt_scrollback(AgwVt* vt, size_t lines) {
    if (!vt) return GHOSTTY_INVALID_VALUE;
    return ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, &lines);
}
static int begin_capture(AgwVt* vt, int use_hold) {
    if (!vt || vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    flush_anchors(vt);
    int hold = use_hold && vt->holding && vt->held && GetTickCount64() - vt->held_since < 1000;
    if (!hold) {
        if (vt->render_invalidated) {
            GhosttyRenderState next = NULL;
            TRY(ghostty_render_state_new(&vt->allocator, &next));
            ghostty_render_state_free(vt->render); vt->render = next; vt->render_invalidated = 0;
        }
        LinkEntry* links = NULL;
        TRY(capture_links(vt, &links));
        int result = ghostty_render_state_begin_update(vt->render, vt->terminal);
        if (result) { free_links(vt, links); return result; }
        // At the pinned revision begin_update captures image placements; only
        // style denormalization is deferred to end_update. Pixel ownership must
        // be established here, while the external terminal lock is still held.
        ImageEntry* images = NULL;
        result = capture_images(vt, vt->render, &images);
        if (result) { ghostty_render_state_end_update(vt->render); free_links(vt, links); vt->last_valid = 0; return result; }
        free_images(vt, vt->render_images); vt->render_images = images;
        free_links(vt, vt->render_links); vt->render_links = links;
    }
    vt->capture = hold ? vt->held : vt->render;
    vt->capture_links = hold ? vt->held_links : vt->render_links;
    vt->capture_images = hold ? vt->held_images : vt->render_images;
    vt->capture_serial = hold ? vt->held_serial : 0;
    vt->capture_busy = 1;
    vt->updating = 1;
    return GHOSTTY_SUCCESS;
}
int agwvt_begin(AgwVt* vt) { return begin_capture(vt, 1); }
int agwvt_begin_viewport(AgwVt* vt, uint32_t offset) {
    if (!vt || vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    size_t history;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_SCROLLBACK_ROWS, &history));
    GhosttyTerminalScrollViewport bottom = { .tag = GHOSTTY_SCROLL_VIEWPORT_BOTTOM };
    GhosttyTerminalScrollViewport viewport = { .tag = GHOSTTY_SCROLL_VIEWPORT_ROW,
        .value = { .row = history - (offset < history ? offset : history) } };
    ghostty_terminal_scroll_viewport(vt->terminal, offset ? viewport : bottom);
    int result = begin_capture(vt, offset == 0);
    // The capture owns its viewport. Parsing, held-frame callbacks and explicit
    // reads continue against the active area after releasing the session lock.
    ghostty_terminal_scroll_viewport(vt->terminal, bottom);
    return result;
}
int agwvt_end(AgwVt* vt) {
    if (!vt || !vt->updating) return GHOSTTY_INVALID_VALUE;
    if (vt->capture == vt->render) TRY(ghostty_render_state_end_update(vt->capture));
    if (!vt->last_valid || vt->last_serial != vt->capture_serial) {
        GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FULL;
        TRY(ghostty_render_state_set(vt->capture, GHOSTTY_RENDER_STATE_OPTION_DIRTY, &dirty));
    }
    vt->updating = 0;
    return GHOSTTY_SUCCESS;
}
int agwvt_clean(AgwVt* vt) {
    if (!vt || vt->updating || !vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    TRY(ghostty_render_state_clean(vt->capture));
    AcquireSRWLockExclusive(&vt->capture_guard);
    vt->last_serial = vt->capture_serial; vt->last_valid = 1; vt->capture_busy = 0;
    if (vt->capture != vt->held && vt->capture != vt->render) { ghostty_render_state_free(vt->capture); free_links(vt, vt->capture_links); free_images(vt, vt->capture_images); }
    vt->capture_images = NULL;
    vt->capture_links = NULL;
    vt->capture = NULL;
    ReleaseSRWLockExclusive(&vt->capture_guard);
    return GHOSTTY_SUCCESS;
}
int agwvt_cursor_default(AgwVt* vt, uint32_t style, uint32_t blink) {
    if (!vt || style > 2 || blink > 1) return GHOSTTY_INVALID_VALUE;
    GhosttyTerminalCursorStyle cursor_style = (GhosttyTerminalCursorStyle)style;
    bool cursor_blink = blink != 0;
    TRY(ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_STYLE, &cursor_style));
    return ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_BLINK, &cursor_blink);
}
int agwvt_colors(AgwVt* vt, uint32_t foreground, uint32_t background) {
    if (!vt || foreground > 0xffffff || background > 0xffffff) return GHOSTTY_INVALID_VALUE;
    GhosttyColorRgb fg = { (uint8_t)(foreground >> 16), (uint8_t)(foreground >> 8), (uint8_t)foreground };
    GhosttyColorRgb bg = { (uint8_t)(background >> 16), (uint8_t)(background >> 8), (uint8_t)background };
    TRY(ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &fg));
    return ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &bg);
}
int agwvt_cursor(AgwVt* vt, uint32_t* col, uint32_t* row, uint32_t* visible, uint32_t* shape) {
    if (!vt || vt->updating || !vt->capture_busy || !col || !row || !visible || !shape) return GHOSTTY_INVALID_VALUE;
    GhosttyRenderStateCursor cursor = GHOSTTY_INIT_SIZED(GhosttyRenderStateCursor);
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_CURSOR, &cursor));
    *col = cursor.viewport_has_value ? cursor.viewport_x : 0;
    *row = cursor.viewport_has_value ? cursor.viewport_y : 0;
    *visible = cursor.viewport_has_value && cursor.visible;
    *shape = (cursor.visual_style == GHOSTTY_RENDER_STATE_CURSOR_VISUAL_STYLE_BAR ? 5u :
        cursor.visual_style == GHOSTTY_RENDER_STATE_CURSOR_VISUAL_STYLE_UNDERLINE ? 3u : 1u) + (cursor.blinking ? 0u : 1u);
    return GHOSTTY_SUCCESS;
}
int agwvt_theme(AgwVt* vt, const uint32_t* palette, uint32_t cursor) {
    if (!vt || !palette || cursor > 0xffffff) return GHOSTTY_INVALID_VALUE;
    GhosttyColorRgb colors[256];
    for (unsigned i = 0; i < 256; i++) {
        uint32_t p = palette[i];
        if (p > 0xffffff) return GHOSTTY_INVALID_VALUE;
        colors[i] = (GhosttyColorRgb){(uint8_t)(p >> 16), (uint8_t)(p >> 8), (uint8_t)p};
    }
    GhosttyColorRgb c = {(uint8_t)(cursor >> 16), (uint8_t)(cursor >> 8), (uint8_t)cursor};
    TRY(ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, &colors));
    return ghostty_terminal_set(vt->terminal, GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, &c);
}
int agwvt_frame_colors(AgwVt* vt, uint32_t* background, uint32_t* cursor) {
    if (!vt || vt->updating || !vt->capture_busy || !background || !cursor) return GHOSTTY_INVALID_VALUE;
    GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_COLORS, &colors));
    *background = rgb(colors.background);
    *cursor = rgb(colors.cursor_has_value ? colors.cursor : colors.foreground);
    return GHOSTTY_SUCCESS;
}
int agwvt_abort(AgwVt* vt) {
    if (!vt || !vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    // Complete the upstream two-phase update before releasing ownership. Dirty
    // state stays set, so a later capture can recover from a managed allocation
    // or conversion failure without silently skipping rows.
    if (vt->updating) TRY(agwvt_end(vt));
    AcquireSRWLockExclusive(&vt->capture_guard);
    vt->last_valid = 0;
    if (vt->capture != vt->held && vt->capture != vt->render) { ghostty_render_state_free(vt->capture); free_links(vt, vt->capture_links); free_images(vt, vt->capture_images); }
    vt->capture_images = NULL;
    vt->capture_links = NULL;
    vt->capture = NULL; vt->capture_busy = 0;
    ReleaseSRWLockExclusive(&vt->capture_guard);
    return GHOSTTY_SUCCESS;
}
static GhosttyKey windows_key(uint32_t vk) {
    if (vk >= 'A' && vk <= 'Z') return GHOSTTY_KEY_A + vk - 'A';
    if (vk >= '0' && vk <= '9') return GHOSTTY_KEY_DIGIT_0 + vk - '0';
    if (vk >= VK_F1 && vk <= VK_F24) return GHOSTTY_KEY_F1 + vk - VK_F1;
    if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) return GHOSTTY_KEY_NUMPAD_0 + vk - VK_NUMPAD0;
    switch (vk) {
        case VK_BACK: return GHOSTTY_KEY_BACKSPACE;
        case VK_TAB: return GHOSTTY_KEY_TAB;
        case VK_RETURN: return GHOSTTY_KEY_ENTER;
        case VK_ESCAPE: return GHOSTTY_KEY_ESCAPE;
        case VK_SPACE: return GHOSTTY_KEY_SPACE;
        case VK_PRIOR: return GHOSTTY_KEY_PAGE_UP;
        case VK_NEXT: return GHOSTTY_KEY_PAGE_DOWN;
        case VK_END: return GHOSTTY_KEY_END;
        case VK_HOME: return GHOSTTY_KEY_HOME;
        case VK_LEFT: return GHOSTTY_KEY_ARROW_LEFT;
        case VK_UP: return GHOSTTY_KEY_ARROW_UP;
        case VK_RIGHT: return GHOSTTY_KEY_ARROW_RIGHT;
        case VK_DOWN: return GHOSTTY_KEY_ARROW_DOWN;
        case VK_INSERT: return GHOSTTY_KEY_INSERT;
        case VK_DELETE: return GHOSTTY_KEY_DELETE;
        case VK_SHIFT: case VK_LSHIFT: return GHOSTTY_KEY_SHIFT_LEFT;
        case VK_RSHIFT: return GHOSTTY_KEY_SHIFT_RIGHT;
        case VK_CONTROL: case VK_LCONTROL: return GHOSTTY_KEY_CONTROL_LEFT;
        case VK_RCONTROL: return GHOSTTY_KEY_CONTROL_RIGHT;
        case VK_MENU: case VK_LMENU: return GHOSTTY_KEY_ALT_LEFT;
        case VK_RMENU: return GHOSTTY_KEY_ALT_RIGHT;
        case VK_LWIN: return GHOSTTY_KEY_META_LEFT;
        case VK_RWIN: return GHOSTTY_KEY_META_RIGHT;
        case VK_CAPITAL: return GHOSTTY_KEY_CAPS_LOCK;
        case VK_NUMLOCK: return GHOSTTY_KEY_NUM_LOCK;
        case VK_MULTIPLY: return GHOSTTY_KEY_NUMPAD_MULTIPLY;
        case VK_ADD: return GHOSTTY_KEY_NUMPAD_ADD;
        case VK_SUBTRACT: return GHOSTTY_KEY_NUMPAD_SUBTRACT;
        case VK_DECIMAL: return GHOSTTY_KEY_NUMPAD_DECIMAL;
        case VK_DIVIDE: return GHOSTTY_KEY_NUMPAD_DIVIDE;
        case VK_OEM_1: return GHOSTTY_KEY_SEMICOLON;
        case VK_OEM_PLUS: return GHOSTTY_KEY_EQUAL;
        case VK_OEM_COMMA: return GHOSTTY_KEY_COMMA;
        case VK_OEM_MINUS: return GHOSTTY_KEY_MINUS;
        case VK_OEM_PERIOD: return GHOSTTY_KEY_PERIOD;
        case VK_OEM_2: return GHOSTTY_KEY_SLASH;
        case VK_OEM_3: return GHOSTTY_KEY_BACKQUOTE;
        case VK_OEM_4: return GHOSTTY_KEY_BRACKET_LEFT;
        case VK_OEM_5: return GHOSTTY_KEY_BACKSLASH;
        case VK_OEM_6: return GHOSTTY_KEY_BRACKET_RIGHT;
        case VK_OEM_7: return GHOSTTY_KEY_QUOTE;
        case VK_OEM_102: return GHOSTTY_KEY_INTL_BACKSLASH;
        default: return GHOSTTY_KEY_UNIDENTIFIED;
    }
}
int agwvt_search(AgwVt* vt, const uint8_t* text, size_t length, uint32_t* output, size_t capacity, size_t* count) {
    if (!vt || (!text && length) || length > 65536 || (!output && capacity) || !count) return GHOSTTY_INVALID_VALUE;
    if (!vt->search) TRY(ghostty_search_new(&vt->allocator, &vt->search, vt->terminal));
    GhosttyString needle = { .ptr = text, .len = length };
    TRY(ghostty_search_set(vt->search, GHOSTTY_SEARCH_OPT_NEEDLE, &needle));
    TRY(ghostty_search_run(vt->search));
    TRY(ghostty_search_get(vt->search, GHOSTTY_SEARCH_DATA_TOTAL_MATCHES, count));
    if (*count > capacity) return GHOSTTY_OUT_OF_SPACE;
    if (!*count) return GHOSTTY_SUCCESS;
    if (*count > 1000000) return GHOSTTY_LIMIT_EXCEEDED;
    size_t bytes = *count * sizeof(GhosttySelection);
    GhosttySelection* selections = (GhosttySelection*)ghostty_alloc(&vt->allocator, bytes);
    if (!selections) return GHOSTTY_OUT_OF_MEMORY;
    GhosttySelectionBuffer buffer = { .ptr = selections, .cap = *count };
    int result = ghostty_search_get(vt->search, GHOSTTY_SEARCH_DATA_MATCHES, &buffer);
    for (size_t i = 0; !result && i < buffer.len; i++) {
        GhosttyPointCoordinate start, end;
        result = ghostty_terminal_point_from_grid_ref(vt->terminal, &selections[i].start, GHOSTTY_POINT_TAG_SCREEN, &start);
        if (!result) result = ghostty_terminal_point_from_grid_ref(vt->terminal, &selections[i].end, GHOSTTY_POINT_TAG_SCREEN, &end);
        GhosttyCell cell; GhosttyCellWide wide;
        if (!result) result = ghostty_grid_ref_cell(&selections[i].end, &cell);
        if (!result) result = ghostty_cell_get(cell, GHOSTTY_CELL_DATA_WIDE, &wide);
        if (!result) { output[i*4] = start.y; output[i*4+1] = start.x; output[i*4+2] = end.y;
            output[i*4+3] = end.x + (wide == GHOSTTY_CELL_WIDE_WIDE ? 1 : 0); }
    }
    ghostty_free(&vt->allocator, (uint8_t*)selections, bytes);
    return result;
}
int agwvt_search_new(AgwVt* vt, const uint8_t* text, size_t length, void** out) {
    if (!vt || !out || (!text && length) || length > 65536) return GHOSTTY_INVALID_VALUE;
    *out = NULL; flush_anchors(vt);
    AgwSearch* s = budget_alloc(vt, sizeof(*s), 0, 0);
    if (!s) return GHOSTTY_OUT_OF_MEMORY;
    *s = (AgwSearch){ .owner = vt };
    int result = ghostty_search_new(&vt->allocator, &s->search, vt->terminal);
    if (!result) result = agwinterm_ghostty_search_unicode(s->search, text, length);
    if (result) { ghostty_search_free(s->search); budget_free(vt, s, sizeof(*s), 0, 0); return result; }
    *out = s; return GHOSTTY_SUCCESS;
}
void agwvt_search_free(void* search) {
    AgwSearch* s = search; if (!s) return;
    AcquireSRWLockExclusive(&s->owner->anchor_guard);
    s->next = s->owner->released_searches; s->owner->released_searches = s;
    ReleaseSRWLockExclusive(&s->owner->anchor_guard);
}
int agwvt_search_feed(void* search, uint32_t* status, size_t* count) {
    AgwSearch* s = search; if (!s || !status || !count) return GHOSTTY_INVALID_VALUE;
    TRY(ghostty_search_feed(s->search));
    TRY(ghostty_search_get(s->search, GHOSTTY_SEARCH_DATA_STATUS, status));
    return ghostty_search_get(s->search, GHOSTTY_SEARCH_DATA_TOTAL_MATCHES, count);
}
int agwvt_search_tick(void* search, uint32_t* status, size_t* count) {
    AgwSearch* s = search; if (!s || !status || !count) return GHOSTTY_INVALID_VALUE;
    TRY(agwinterm_ghostty_search_tick(s->search, status));
    return ghostty_search_get(s->search, GHOSTTY_SEARCH_DATA_TOTAL_MATCHES, count);
}
int agwvt_search_matches(void* search, uint32_t* output, size_t capacity, size_t* count) {
    AgwSearch* s = search; if (!s || (!output && capacity) || !count || capacity > 100000) return GHOSTTY_INVALID_VALUE;
    GhosttySelection selections[128];
    TRY(agwinterm_ghostty_search_matches(s->search, 0, selections, 0, count));
    size_t limit = *count < capacity ? *count : capacity;
    for (size_t offset = 0; offset < limit; offset += 128) {
        size_t batch = limit - offset < 128 ? limit - offset : 128;
        TRY(agwinterm_ghostty_search_matches(s->search, offset, selections, batch, count));
        for (size_t i = 0; i < batch; i++) {
            GhosttyPointCoordinate start, end; GhosttyCell cell; GhosttyCellWide wide;
            TRY(ghostty_terminal_point_from_grid_ref(s->owner->terminal, &selections[i].start, GHOSTTY_POINT_TAG_SCREEN, &start));
            TRY(ghostty_terminal_point_from_grid_ref(s->owner->terminal, &selections[i].end, GHOSTTY_POINT_TAG_SCREEN, &end));
            TRY(ghostty_grid_ref_cell(&selections[i].end, &cell));
            TRY(ghostty_cell_get(cell, GHOSTTY_CELL_DATA_WIDE, &wide));
            size_t j = (offset + i) * 4;
            output[j] = start.y; output[j+1] = start.x; output[j+2] = end.y;
            output[j+3] = end.x + (wide == GHOSTTY_CELL_WIDE_WIDE ? 1 : 0);
        }
    }
    return GHOSTTY_SUCCESS;
}
int agwvt_mouse(AgwVt* vt, uint32_t action, uint32_t button, uint32_t mods,
    float x, float y, uint32_t cw, uint32_t ch, uint8_t* output, size_t capacity, size_t* length) {
    if (!vt || action > 2 || button > 11 || mods > 1023 || !cw || !ch || cw > 65535 || ch > 65535 ||
        (!output && capacity) || !length) return GHOSTTY_INVALID_VALUE;
    uint16_t cols, rows;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &cols));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_ROWS, &rows));
    GhosttyMouseEncoderSize size = { .size = sizeof(size), .screen_width = cols * cw,
        .screen_height = rows * ch, .cell_width = cw, .cell_height = ch };
    ghostty_mouse_encoder_setopt_from_terminal(vt->mouse_encoder, vt->terminal);
    TRY(ghostty_mouse_encoder_setopt(vt->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size));
    bool pressed = action != GHOSTTY_MOUSE_ACTION_RELEASE && button >= 1 && button <= 3;
    TRY(ghostty_mouse_encoder_setopt(vt->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED, &pressed));
    TRY(ghostty_mouse_event_set_action(vt->mouse_event, (GhosttyMouseAction)action));
    if (button) { TRY(ghostty_mouse_event_set_button(vt->mouse_event, (GhosttyMouseButton)button)); }
    else ghostty_mouse_event_clear_button(vt->mouse_event);
    ghostty_mouse_event_set_mods(vt->mouse_event, (GhosttyMods)mods);
    ghostty_mouse_event_set_position(vt->mouse_event, (GhosttyMousePosition){x, y});
    return ghostty_mouse_encoder_encode(vt->mouse_encoder, vt->mouse_event, (char*)output, capacity, length);
}
int agwvt_paste(AgwVt* vt, uint8_t* text, size_t text_length, uint8_t* output, size_t capacity, size_t* length) {
    if (!vt || text_length > 16u * 1024u * 1024u || (!text && text_length) || (!output && capacity) || !length)
        return GHOSTTY_INVALID_VALUE;
    GhosttyTerminalModeConfig mode = { .mode = ghostty_mode_new(2004, false) };
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode));
    return ghostty_paste_encode((char*)text, text_length, mode.value, (char*)output, capacity, length);
}
int agwvt_key(AgwVt* vt, uint32_t vk, uint32_t action, uint32_t mods,
    uint32_t consumed, uint32_t unshifted, const uint8_t* text, size_t text_length,
    uint8_t* output, size_t capacity, size_t* length) {
    if (!vt || action > 2 || mods > 1023 || consumed > 1023 || text_length > 4096 ||
        (!text && text_length) || (!output && capacity) || !length) return GHOSTTY_INVALID_VALUE;
    ghostty_key_encoder_setopt_from_terminal(vt->key_encoder, vt->terminal);
    TRY(ghostty_key_event_set_action(vt->key_event, (GhosttyKeyAction)action));
    TRY(ghostty_key_event_set_key(vt->key_event, windows_key(vk)));
    ghostty_key_event_set_mods(vt->key_event, (GhosttyMods)mods);
    ghostty_key_event_set_consumed_mods(vt->key_event, (GhosttyMods)consumed);
    ghostty_key_event_set_unshifted_codepoint(vt->key_event, unshifted);
    ghostty_key_event_set_utf8(vt->key_event, (const char*)text, text_length);
    int result = ghostty_key_encoder_encode(vt->key_encoder, vt->key_event, (char*)output, capacity, length);
    ghostty_key_event_set_utf8(vt->key_event, NULL, 0); // never retain a managed pointer
    return result;
}
int agwvt_animation_tick(AgwVt* vt, uint64_t* deadline) {
    if (!vt || !deadline || vt->capture_busy) return GHOSTTY_INVALID_VALUE;
    *deadline = 0;
    uint64_t now = GetTickCount64();
    if (vt->holding && now - vt->held_since < 1000) return GHOSTTY_SUCCESS;
    GhosttyKittyGraphics graphics;
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_KITTY_GRAPHICS, &graphics));
    int result = ghostty_kitty_graphics_animation_tick(graphics, now, deadline);
    return result == GHOSTTY_NO_VALUE ? GHOSTTY_SUCCESS : result;
}
int agwvt_images(AgwVt* vt, AgwVtImage* images, size_t capacity, size_t* count, uint32_t stride) {
    if (!vt || !vt->capture_busy || vt->updating || !count || (!images && capacity) || stride != sizeof(AgwVtImage)) return GHOSTTY_INVALID_VALUE;
    *count = 0;
    for (ImageEntry* entry = vt->capture_images; entry; entry = entry->next) {
        if (*count < capacity) images[*count] = entry->value;
        (*count)++;
    }
    return *count > capacity ? GHOSTTY_OUT_OF_SPACE : GHOSTTY_SUCCESS;
}
int agwvt_image_pixels(AgwVt* vt, uint64_t generation, uint8_t* pixels, size_t capacity) {
    if (!vt || !vt->capture_busy || vt->updating || !pixels) return GHOSTTY_INVALID_VALUE;
    for (ImageEntry* entry = vt->capture_images; entry; entry = entry->next) {
        if (entry->value.generation != generation) continue;
        if (capacity < entry->pixels->length) return GHOSTTY_OUT_OF_SPACE;
        memcpy(pixels, entry->pixels->data, entry->pixels->length);
        return GHOSTTY_SUCCESS;
    }
    return GHOSTTY_NO_VALUE;
}
int agwvt_text(AgwVt* vt, uint32_t kind, uint8_t* text, size_t capacity, size_t* length) {
    if (!vt || !length || kind > 1) return GHOSTTY_INVALID_VALUE;
    GhosttyString value;
    TRY(ghostty_terminal_get(vt->terminal, kind ? GHOSTTY_TERMINAL_DATA_PWD : GHOSTTY_TERMINAL_DATA_TITLE, &value));
    *length = value.len;
    if (capacity < value.len || (!text && value.len)) return GHOSTTY_OUT_OF_SPACE;
    if (value.len) memcpy(text, value.ptr, value.len);
    return GHOSTTY_SUCCESS;
}
static size_t utf8(uint32_t cp, uint8_t* out) {
    if (cp < 0x80) { out[0] = (uint8_t)cp; return 1; }
    if (cp < 0x800) { out[0] = 0xc0 | (cp >> 6); out[1] = 0x80 | (cp & 63); return 2; }
    if (cp < 0x10000) { out[0] = 0xe0 | (cp >> 12); out[1] = 0x80 | ((cp >> 6) & 63); out[2] = 0x80 | (cp & 63); return 3; }
    out[0] = 0xf0 | (cp >> 18); out[1] = 0x80 | ((cp >> 12) & 63); out[2] = 0x80 | ((cp >> 6) & 63); out[3] = 0x80 | (cp & 63); return 4;
}
int agwvt_read_row(AgwVt* vt, uint32_t y, uint32_t history, AgwVtCell* cells, size_t capacity,
    int32_t* wrap, uint8_t* text, size_t text_capacity, size_t* text_used) {
    if (!vt || !cells || !wrap || !text || !text_used || text_capacity > UINT32_MAX) return GHOSTTY_INVALID_VALUE;
    uint16_t cols; TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLS, &cols));
    if (capacity < cols) return GHOSTTY_OUT_OF_SPACE;
    GhosttyColorRgb fg, bg; GhosttyColorRgb palette[256];
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_FOREGROUND, &fg));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_BACKGROUND, &bg));
    TRY(ghostty_terminal_get(vt->terminal, GHOSTTY_TERMINAL_DATA_COLOR_PALETTE, &palette));
    *text_used = 0; *wrap = -1;
    for (uint16_t x = 0; x < cols; x++) {
        GhosttyPoint point = { .tag = history ? GHOSTTY_POINT_TAG_HISTORY : GHOSTTY_POINT_TAG_ACTIVE,
            .value = { .coordinate = { .x = x, .y = y } } };
        GhosttyGridRef ref = {0};
        TRY(ghostty_terminal_grid_ref(vt->terminal, point, &ref));
        GhosttyCell raw; GhosttyRow row; GhosttyCellWide wide; GhosttyCellContentTag tag;
        TRY(ghostty_grid_ref_cell(&ref, &raw));
        if (x == 0) { bool wrapped; TRY(ghostty_grid_ref_row(&ref, &row)); TRY(ghostty_row_get(row, GHOSTTY_ROW_DATA_WRAP, &wrapped)); *wrap = wrapped ? cols : -1; }
        AgwVtCell* dst = &cells[x]; memset(dst, 0, sizeof(*dst));
        TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CODEPOINT, &dst->rune));
        TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide));
        TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CONTENT_TAG, &tag));
        if (!dst->rune) dst->rune = ' ';
        dst->width = wide == GHOSTTY_CELL_WIDE_WIDE ? 2 : wide == GHOSTTY_CELL_WIDE_SPACER_TAIL ? 0 : 1;
        if (wide == GHOSTTY_CELL_WIDE_SPACER_HEAD && *wrap >= 0) *wrap = cols - 1;
        GhosttyStyle style = {0};
        TRY(ghostty_grid_ref_style(&ref, &style));
        dst->attrs = style_attributes(style);
        dst->fg_kind = style.fg_color.tag; dst->bg_kind = style.bg_color.tag;
        dst->fg = style.fg_color.tag == 2 ? rgb(style.fg_color.value.rgb) : style.fg_color.tag == 1 ? rgb(palette[style.fg_color.value.palette]) : rgb(fg);
        dst->bg = style.bg_color.tag == 2 ? rgb(style.bg_color.value.rgb) : style.bg_color.tag == 1 ? rgb(palette[style.bg_color.value.palette]) : rgb(bg);
        dst->fg_value = dst->fg_kind == 1 ? style.fg_color.value.palette : dst->fg;
        dst->bg_value = dst->bg_kind == 1 ? style.bg_color.value.palette : dst->bg;
        if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_PALETTE) {
            uint8_t index; TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_COLOR_PALETTE, &index));
            dst->bg_kind = 1; dst->bg_value = index; dst->bg = rgb(palette[index]);
        } else if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_RGB) {
            GhosttyColorRgb color; TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_COLOR_RGB, &color));
            dst->bg_kind = 2; dst->bg = dst->bg_value = rgb(color);
        }
        if (tag == GHOSTTY_CELL_CONTENT_CODEPOINT_GRAPHEME) {
            uint32_t stack[256]; size_t count;
            int result = ghostty_grid_ref_graphemes(&ref, stack, 256, &count);
            if (result != GHOSTTY_SUCCESS && result != GHOSTTY_OUT_OF_SPACE) return result;
            if (count > (text_capacity - *text_used) / 4) return GHOSTTY_OUT_OF_SPACE;
            uint32_t* scalars = stack;
            size_t allocation = count * sizeof(uint32_t);
            if (result == GHOSTTY_OUT_OF_SPACE) {
                scalars = budget_alloc(vt, allocation, 2, 0);
                if (!scalars) return GHOSTTY_OUT_OF_MEMORY;
                result = ghostty_grid_ref_graphemes(&ref, scalars, count, &count);
                if (result) { budget_free(vt, scalars, allocation, 2, 0); return result; }
            }
            dst->text_offset = (uint32_t)*text_used;
            for (size_t i = 0; i < count; i++) *text_used += utf8(scalars[i], text + *text_used);
            if (scalars != stack) budget_free(vt, scalars, allocation, 2, 0);
            dst->text_length = (uint32_t)*text_used - dst->text_offset;
        }
        bool link; TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_HAS_HYPERLINK, &link));
        if (link) {
            size_t length; TRY(ghostty_grid_ref_hyperlink_uri(&ref, text + *text_used, text_capacity - *text_used, &length));
            dst->link_offset = (uint32_t)*text_used; dst->link_length = (uint32_t)length; *text_used += length;
        }
    }
    return GHOSTTY_SUCCESS;
}
int agwvt_cells(AgwVt* vt, AgwVtCell* cells, size_t capacity, uint8_t* dirty, int32_t* wraps,
    size_t row_capacity, uint8_t* text, size_t text_capacity, size_t* text_used) {
    if (!vt || vt->updating || !vt->capture_busy || !cells || !dirty || !wraps || !text || !text_used) return GHOSTTY_INVALID_VALUE;
    uint16_t cols, rows;
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_COLS, &cols));
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_ROWS, &rows));
    if (capacity < (size_t)cols * rows || row_capacity < rows || text_capacity > UINT32_MAX) return GHOSTTY_OUT_OF_SPACE;
    memset(dirty, 0, rows); *text_used = 0;
    GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_COLORS, &colors));
    TRY(ghostty_render_state_get(vt->capture, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &vt->rows));
    uint16_t y;
    LinkEntry* link = vt->capture_links;
    while (ghostty_render_state_row_iterator_next_dirty(vt->rows, &y)) {
        if (y >= rows) return GHOSTTY_INVALID_VALUE;
        dirty[y] = 1;
        GhosttyRow row; bool wrap;
        TRY(ghostty_render_state_row_get(vt->rows, GHOSTTY_RENDER_STATE_ROW_DATA_RAW, &row));
        TRY(ghostty_row_get(row, GHOSTTY_ROW_DATA_WRAP, &wrap));
        wraps[y] = wrap ? cols : -1;
        TRY(ghostty_render_state_row_get(vt->rows, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &vt->cells));
        uint16_t x = 0;
        while (ghostty_render_state_row_cells_next(vt->cells)) {
            if (x >= cols) return GHOSTTY_INVALID_VALUE;
            AgwVtCell* dst = &cells[(size_t)y * cols + x++];
            memset(dst, 0, sizeof(*dst));
            GhosttyCell raw; GhosttyCellWide wide; GhosttyStyle style = {0};
            TRY(ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
            TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CODEPOINT, &dst->rune));
            TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide));
            size_t index = (size_t)y * cols + x - 1;
            while (link && link->index < index) link = link->next;
            if (link && link->index == index) {
                if (link->length > text_capacity - *text_used) return GHOSTTY_OUT_OF_SPACE;
                dst->link_offset = (uint32_t)*text_used; dst->link_length = (uint32_t)link->length;
                memcpy(text + *text_used, link->uri, link->length); *text_used += link->length;
            }
            if (!dst->rune) dst->rune = ' ';
            dst->width = wide == GHOSTTY_CELL_WIDE_WIDE ? 2 : wide == GHOSTTY_CELL_WIDE_SPACER_TAIL ? 0 : 1;
            if (wide == GHOSTTY_CELL_WIDE_SPACER_HEAD && wrap) wraps[y] = cols - 1;
            TRY(ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style));
            dst->attrs = style_attributes(style);
            GhosttyColorRgb fg = colors.foreground, bg = colors.background;
            ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &fg);
            ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &bg);
            dst->fg = rgb(fg); dst->bg = rgb(bg);
            dst->fg_kind = style.fg_color.tag; dst->bg_kind = style.bg_color.tag;
            dst->fg_value = style.fg_color.tag == GHOSTTY_STYLE_COLOR_PALETTE ? style.fg_color.value.palette : dst->fg;
            dst->bg_value = style.bg_color.tag == GHOSTTY_STYLE_COLOR_PALETTE ? style.bg_color.value.palette : dst->bg;
            GhosttyCellContentTag tag;
            TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CONTENT_TAG, &tag));
            if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_PALETTE) {
                uint8_t index; TRY(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_COLOR_PALETTE, &index));
                dst->bg_kind = 1; dst->bg_value = index;
            } else if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_RGB) { dst->bg_kind = 2; dst->bg_value = dst->bg; }
            uint32_t count;
            TRY(ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN, &count));
            if (count > 1) {
                GhosttyBuffer buf = { .ptr = text + *text_used, .cap = text_capacity - *text_used };
                TRY(ghostty_render_state_row_cells_get(vt->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &buf));
                dst->text_offset = (uint32_t)*text_used; dst->text_length = (uint32_t)buf.len; *text_used += buf.len;
            }
        }
    }
    return GHOSTTY_SUCCESS;
}
