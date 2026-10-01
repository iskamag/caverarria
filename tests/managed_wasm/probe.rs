// Executed as wasm32-unknown-unknown in .NET. No OS/WASI bindings.
#[link(wasm_import_module = "env")]
extern "C" { fn host_delta(value: i32) -> i32; }

#[no_mangle]
pub extern "C" fn call_host(value: i32) -> i32 { unsafe { host_delta(value) } }

#[no_mangle]
pub extern "C" fn f32_mix(a: f32, b: f32) -> f32 { a * 1.25 + b }
#[no_mangle]
pub extern "C" fn f64_mix(a: f64, b: f64) -> f64 { a.sqrt() + b * 0.5 }

#[inline(never)]
fn double(v: i32) -> i32 { v.wrapping_mul(2) }
#[inline(never)]
fn triple(v: i32) -> i32 { v.wrapping_mul(3) }
static FUNCTIONS: [fn(i32) -> i32; 2] = [double, triple];
#[no_mangle]
pub extern "C" fn indirect(which: usize, value: i32) -> i32 {
    let function = unsafe { std::ptr::read_volatile(FUNCTIONS.as_ptr().add(which & 1)) };
    function(value)
}

#[no_mangle]
pub extern "C" fn alloc(size: usize) -> *mut u8 {
    Box::into_raw(vec![0u8; size].into_boxed_slice()) as *mut u8
}
#[no_mangle]
pub unsafe extern "C" fn free(ptr: *mut u8, size: usize) {
    drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, size)))
}
#[no_mangle]
pub unsafe extern "C" fn copy(dst: *mut u8, src: *const u8, size: usize) { std::ptr::copy(src, dst, size) }
#[no_mangle]
pub unsafe extern "C" fn fill(dst: *mut u8, byte: u8, size: usize) { std::ptr::write_bytes(dst, byte, size) }
#[no_mangle]
pub extern "C" fn grow(pages: usize) -> usize { core::arch::wasm32::memory_grow::<0>(pages) }
#[no_mangle]
pub extern "C" fn pages() -> usize { core::arch::wasm32::memory_size::<0>() }

#[no_mangle]
pub unsafe extern "C" fn raster(ptr: *mut u32, width: usize, height: usize, frame: u32) {
    for y in 0..height {
        for x in 0..width {
            let red = (x as u32).wrapping_add(frame) & 255;
            let green = (y as u32).wrapping_mul(3).wrapping_add(frame) & 255;
            let blue = ((x as u32) ^ (y as u32) ^ frame) & 255;
            *ptr.add(y * width + x) = red | (green << 8) | (blue << 16) | 0xff000000;
        }
    }
}
