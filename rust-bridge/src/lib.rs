//! C ABI for the real doukutsu-rs engine. All calls for a handle must use one thread.
use doukutsu_rs::cavebridge::Runtime;
use std::cell::RefCell;
use std::ffi::{c_char, CStr, CString};
thread_local! { static ERROR: RefCell<CString> = RefCell::new(CString::new("").unwrap()); }
fn error(message: String) {
    ERROR.with(|x| *x.borrow_mut() = CString::new(message.replace('\0', " ")).unwrap());
}
unsafe fn string(ptr: *const c_char) -> Result<String, String> {
    if ptr.is_null() {
        return Err("Null string argument".into());
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map(str::to_owned)
        .map_err(|e| e.to_string())
}
#[no_mangle]
pub unsafe extern "C" fn cave_create(
    data: *const c_char,
    save: *const c_char,
    width: i32,
    height: i32,
) -> *mut Runtime {
    let result =
        std::panic::catch_unwind(|| Runtime::new(&string(data)?, &string(save)?, width, height));
    match result {
        Ok(Ok(runtime)) => Box::into_raw(Box::new(runtime)),
        Ok(Err(e)) => {
            error(e);
            std::ptr::null_mut()
        }
        Err(_) => {
            error("Engine panicked during initialization".into());
            std::ptr::null_mut()
        }
    }
}
#[no_mangle]
pub extern "C" fn cave_last_error() -> *const c_char {
    ERROR.with(|x| x.borrow().as_ptr())
}
#[no_mangle]
pub unsafe extern "C" fn cave_command(
    runtime: *mut Runtime,
    request: *const c_char,
) -> *const c_char {
    if runtime.is_null() {
        error("Null runtime".into());
        return cave_last_error();
    }
    let runtime = &mut *runtime;
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        runtime.command(&string(request)?)
    }));
    let response = match result {
        Ok(Ok(value)) => value,
        Ok(Err(e)) => serde_json::json!({"ok":false,"error":e}).to_string(),
        Err(_) => serde_json::json!({"ok":false,"error":"Engine panicked; discard this handle"})
            .to_string(),
    };
    runtime.response = CString::new(response.replace('\0', " ")).unwrap();
    runtime.response.as_ptr()
}
#[no_mangle]
pub unsafe extern "C" fn cave_pixels(runtime: *const Runtime, layer: i32) -> *const u8 {
    if runtime.is_null() || !(0..=2).contains(&layer) {
        return std::ptr::null();
    }
    (&*runtime).pixels(layer as usize)
}
#[no_mangle]
pub unsafe extern "C" fn cave_destroy(runtime: *mut Runtime) {
    if !runtime.is_null() {
        drop(Box::from_raw(runtime));
    }
}

#[cfg(any(feature = "portable", feature = "pull-audio"))]
#[no_mangle]
pub extern "C" fn cave_audio_rate() -> i32 {
    48000
}

#[cfg(any(feature = "portable", feature = "pull-audio"))]
#[no_mangle]
pub unsafe extern "C" fn cave_audio(runtime: *mut Runtime, frames: i32) -> *const i16 {
    if runtime.is_null() || !(1..=8192).contains(&frames) {
        return std::ptr::null();
    }
    (&mut *runtime).audio(frames as usize)
}

#[cfg(any(feature = "portable", feature = "pull-audio"))]
#[no_mangle]
pub unsafe extern "C" fn cave_audio_length(runtime: *const Runtime) -> i32 {
    if runtime.is_null() {
        return 0;
    }
    (&*runtime).audio_length() as i32
}

#[cfg(feature = "portable")]
#[no_mangle]
pub extern "C" fn cave_alloc(length: i32) -> *mut u8 {
    if length <= 0 {
        return std::ptr::null_mut();
    }
    Box::into_raw(vec![0u8; length as usize].into_boxed_slice()) as *mut u8
}

#[cfg(feature = "portable")]
#[no_mangle]
pub unsafe extern "C" fn cave_free(bytes: *mut u8, length: i32) {
    if !bytes.is_null() && length > 0 {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(
            bytes,
            length as usize,
        )));
    }
}

#[cfg(feature = "portable")]
#[no_mangle]
pub extern "C" fn cave_set_time(seconds: u64) {
    doukutsu_rs::cavebridge::portable::set_time(seconds);
}

#[cfg(feature = "portable")]
#[no_mangle]
pub extern "C" fn cave_fs_revision(save: i32) -> u64 {
    doukutsu_rs::cavebridge::portable::revision(save != 0)
}

#[cfg(feature = "portable")]
#[no_mangle]
pub unsafe extern "C" fn cave_fs_put(
    path: *const c_char,
    bytes: *const u8,
    length: i32,
    save: i32,
) -> i32 {
    if length < 0 || (length > 0 && bytes.is_null()) {
        return -1;
    }
    let bytes = if length == 0 {
        &[][..]
    } else {
        std::slice::from_raw_parts(bytes, length as usize)
    };
    match string(path).and_then(|p| {
        doukutsu_rs::cavebridge::portable::put(&p, bytes, save != 0).map_err(|e| e.to_string())
    }) {
        Ok(()) => 0,
        Err(e) => {
            error(e);
            -1
        }
    }
}

#[cfg(feature = "portable")]
#[no_mangle]
pub unsafe extern "C" fn cave_fs_get(path: *const c_char, save: i32) -> *const u8 {
    string(path)
        .ok()
        .map(|p| doukutsu_rs::cavebridge::portable::file_pointer(&p, save != 0).0)
        .unwrap_or(std::ptr::null())
}

#[cfg(feature = "portable")]
#[no_mangle]
pub unsafe extern "C" fn cave_fs_len(path: *const c_char, save: i32) -> i32 {
    string(path)
        .ok()
        .map(|p| doukutsu_rs::cavebridge::portable::file_pointer(&p, save != 0).1 as i32)
        .unwrap_or(0)
}

#[cfg(feature = "portable")]
thread_local! { static FILE_LIST: RefCell<CString> = RefCell::new(CString::new("[]").unwrap()); }

#[cfg(feature = "portable")]
#[no_mangle]
pub extern "C" fn cave_fs_list(save: i32) -> *const c_char {
    FILE_LIST.with(|list| {
        *list.borrow_mut() = CString::new(
            serde_json::to_string(&doukutsu_rs::cavebridge::portable::files(save != 0)).unwrap(),
        )
        .unwrap();
        list.borrow().as_ptr()
    })
}

#[cfg(feature = "portable")]
#[no_mangle]
pub unsafe extern "C" fn cave_extract_original(bytes: *const u8, length: i32) -> i32 {
    if bytes.is_null() || length <= 0 {
        return -1;
    }
    match doukutsu_rs::cavebridge::portable::extract_original(std::slice::from_raw_parts(
        bytes,
        length as usize,
    )) {
        Ok(()) => 0,
        Err(e) => {
            error(e.to_string());
            -1
        }
    }
}
