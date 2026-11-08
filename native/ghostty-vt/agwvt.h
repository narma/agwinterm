#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef _WIN32
#define AGWVT_API __declspec(dllexport)
#else
#define AGWVT_API __attribute__((visibility("default")))
#endif

typedef struct AgwVt AgwVt;
typedef void (*AgwVtClipboard)(void*, uint32_t, uint32_t, const void*, const void*, size_t, const void*, size_t, uint32_t);
AGWVT_API void agwvt_clipboard_callback(AgwVt*, AgwVtClipboard);
AGWVT_API void agwvt_clipboard_reply(const void*, uint32_t, uint32_t, const void*, size_t, const void*, size_t);
// All public fields are fixed-width, without C bool, enums or borrowed pointers.
typedef struct {
    uint32_t rune, fg, bg, attrs, width;
    uint32_t fg_kind, fg_value, bg_kind, bg_value;
    uint32_t text_offset, text_length;
    uint32_t link_offset, link_length;
    uint32_t underline_style, underline_color, underline_kind, underline_value;
} AgwVtCell;
typedef struct {
    uint32_t cols, rows, cursor_col, cursor_row, cursor_visible, alt_screen;
    uint32_t history, keyboard_flags, modes, background, default_background;
    uint64_t scroll_epoch;
    uint32_t cursor_shape, scroll_top, scroll_bottom, background_override;
} AgwVtInfo;
typedef struct { int32_t prompt, command, output, end, exit_code, has_exit_code; } AgwVtMark;
typedef struct {
    uint64_t generation;
    uint32_t id, layer;
    int32_t z, col, row;
    uint32_t offset_x, offset_y, width, height, source_x, source_y, source_width, source_height;
    uint32_t image_width, image_height, format, data_length;
} AgwVtImage;
typedef void (*AgwVtEvent)(void* context, uint32_t kind, const uint8_t* data, size_t len);

AGWVT_API uint32_t agwvt_abi(uint32_t* cell_size, uint32_t* info_size);
AGWVT_API const char* agwvt_revision(void);
// Optional diagnostics: Ghostty budget allocator only (excludes CRT/COM and AgwVt wrapper).
AGWVT_API void agwvt_allocation_stats(uint64_t* current, uint64_t* peak, uint64_t* allocated, uint64_t* allocations, uint64_t* frees);
// Page bytes bypass the allocator. Images are reported separately and may also
// be included in allocator counters; do not sum image bytes into that total.
AGWVT_API void agwvt_page_stats(uint64_t* resident, uint64_t* virtual_bytes, uint64_t* image_bytes);
AGWVT_API int agwvt_new(uint32_t cols, uint32_t rows, AgwVtEvent event, void* context, AgwVt** out);
AGWVT_API void agwvt_free(AgwVt* vt);
AGWVT_API int agwvt_feed(AgwVt* vt, const uint8_t* bytes, size_t len);
AGWVT_API int agwvt_resize(AgwVt* vt, uint32_t cols, uint32_t rows, uint32_t cw, uint32_t ch);
AGWVT_API int agwvt_info(AgwVt* vt, AgwVtInfo* out);
AGWVT_API int agwvt_scrollback(AgwVt* vt, size_t lines);
AGWVT_API int agwvt_prepend_history(AgwVt* vt, uint32_t source_cols, const uint8_t* bytes, size_t length, uint32_t max_rows, size_t* added);
AGWVT_API int agwvt_begin(AgwVt* vt);
AGWVT_API int agwvt_begin_viewport(AgwVt* vt, uint32_t offset);
AGWVT_API int agwvt_search(AgwVt* vt, const uint8_t* text, size_t length, uint32_t* output, size_t capacity, size_t* count);
AGWVT_API int agwvt_search_new(AgwVt* vt, const uint8_t* text, size_t length, void** out);
AGWVT_API void agwvt_search_free(void* search);
AGWVT_API int agwvt_search_feed(void* search, uint32_t* status, size_t* count);
AGWVT_API int agwvt_search_tick(void* search, uint32_t* status, size_t* count);
AGWVT_API int agwvt_search_matches(void* search, uint32_t* output, size_t capacity, size_t* count);
AGWVT_API int agwvt_end(AgwVt* vt);
// Metadata and pixels belong to the active capture, not the mutable terminal.
AGWVT_API int agwvt_images(AgwVt* vt, AgwVtImage* images, size_t capacity, size_t* count, uint32_t stride);
AGWVT_API int agwvt_image_pixels(AgwVt* vt, uint64_t generation, uint8_t* pixels, size_t capacity);
AGWVT_API int agwvt_animation_tick(AgwVt* vt, uint64_t* deadline);
AGWVT_API int agwvt_mouse(AgwVt* vt, uint32_t action, uint32_t button, uint32_t mods,
    float x, float y, uint32_t cw, uint32_t ch, uint8_t* output, size_t capacity, size_t* length);
AGWVT_API int agwvt_paste(AgwVt* vt, uint8_t* text, size_t text_length, uint8_t* output, size_t capacity, size_t* length);
// Windows physical key identity; modifiers use Ghostty's Shift/Ctrl/Alt/Super bits.
AGWVT_API int agwvt_key(AgwVt* vt, uint32_t vk, uint32_t action, uint32_t mods,
    uint32_t consumed, uint32_t unshifted, const uint8_t* text, size_t text_length,
    uint8_t* output, size_t capacity, size_t* length);
AGWVT_API int agwvt_cursor(AgwVt* vt, uint32_t* col, uint32_t* row, uint32_t* visible, uint32_t* shape);
AGWVT_API int agwvt_cursor_default(AgwVt* vt, uint32_t style, uint32_t blink);
AGWVT_API int agwvt_colors(AgwVt* vt, uint32_t foreground, uint32_t background);
AGWVT_API int agwvt_theme(AgwVt* vt, const uint32_t* palette, uint32_t cursor);
AGWVT_API int agwvt_frame_colors(AgwVt* vt, uint32_t* background, uint32_t* cursor);
// Only dirty rows are written. Text offsets refer to this call's UTF-8 arena.
// Call clean only after a successful transfer; OUT_OF_SPACE permits retry.
AGWVT_API int agwvt_cells(AgwVt* vt, AgwVtCell* cells, size_t capacity,
    uint8_t* dirty, int32_t* wraps, size_t rows, uint8_t* text, size_t text_capacity, size_t* text_used);
AGWVT_API int agwvt_clean(AgwVt* vt);
// Release a failed transfer without consuming its dirty rows.
AGWVT_API int agwvt_abort(AgwVt* vt);
AGWVT_API int agwvt_read_row(AgwVt* vt, uint32_t row, uint32_t history, AgwVtCell* cells,
    size_t capacity, int32_t* wrap, uint8_t* text, size_t text_capacity, size_t* text_used);
AGWVT_API int agwvt_text(AgwVt* vt, uint32_t kind, uint8_t* text, size_t capacity, size_t* length);
AGWVT_API int agwvt_marks(AgwVt* vt, AgwVtMark* marks, size_t capacity, size_t* count);
AGWVT_API int agwvt_anchor_new(AgwVt* vt, uint32_t col, uint32_t row, void** anchor);
AGWVT_API int agwvt_select_bounds(AgwVt* vt, uint32_t kind, uint32_t col, uint32_t row,
    const uint32_t* delimiters, size_t count, uint32_t* bounds);
AGWVT_API int agwvt_selection_drag(AgwVt* vt, uint32_t ar, uint32_t ac, uint32_t fr, uint32_t fc,
    uint32_t behavior, uint32_t rectangle, const uint32_t* delimiters, size_t count, uint32_t* bounds);
AGWVT_API int agwvt_format_selection(AgwVt* vt, uint32_t ar, uint32_t ac, uint32_t fr, uint32_t fc,
    uint32_t block, uint32_t format, uint8_t* output, size_t capacity, size_t* length);
AGWVT_API int agwvt_anchor_point(void* anchor, uint32_t* col, uint32_t* row);
AGWVT_API void agwvt_anchor_free(void* anchor);
AGWVT_API int agwvt_snapshot(AgwVt* vt, uint8_t** bytes, size_t* length);
AGWVT_API void agwvt_snapshot_free(AgwVt* vt, uint8_t* bytes, size_t length);
AGWVT_API int agwvt_restore(AgwVt* vt, const uint8_t* bytes, size_t length);
