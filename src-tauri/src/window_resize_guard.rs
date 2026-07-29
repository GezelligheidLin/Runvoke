//! Reduces the trailing-edge gap caused by WebView2's windowed hosting on Windows.

use tauri::{Runtime, WebviewWindow};

#[cfg(any(windows, test))]
const fn colorref_from_rgb(red: u8, green: u8, blue: u8) -> u32 {
    (red as u32) | ((green as u32) << 8) | ((blue as u32) << 16)
}

pub fn update_color(red: u8, green: u8, blue: u8) {
    #[cfg(windows)]
    imp::store_color(red, green, blue);

    #[cfg(not(windows))]
    let _ = (red, green, blue);
}

pub fn install<R: Runtime>(window: &WebviewWindow<R>, color: (u8, u8, u8)) -> Result<(), String> {
    update_color(color.0, color.1, color.2);

    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取窗口句柄：{error}"))?;
        imp::install(hwnd.0 as isize)
    }

    #[cfg(not(windows))]
    {
        let _ = window;
        Ok(())
    }
}

#[cfg(windows)]
mod imp {
    use std::sync::atomic::{AtomicU32, Ordering};

    use windows_sys::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        CreateSolidBrush, DeleteObject, FillRect, RedrawWindow, HDC, RDW_ALLCHILDREN,
        RDW_INVALIDATE, RDW_UPDATENOW,
    };
    use windows_sys::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        FindWindowExW, GetClientRect, SetWindowPos, SIZE_MINIMIZED, SWP_NOACTIVATE, SWP_NOZORDER,
        WM_ERASEBKGND, WM_EXITSIZEMOVE, WM_SIZE,
    };

    const SUBCLASS_ID: usize = 0x5255_4E56;
    const WRY_WEBVIEW_CLASS: [u16; 12] = [87, 82, 89, 95, 87, 69, 66, 86, 73, 69, 87, 0];
    static PAINT_COLOR: AtomicU32 = AtomicU32::new(super::colorref_from_rgb(247, 245, 241));

    pub(super) fn store_color(red: u8, green: u8, blue: u8) {
        PAINT_COLOR.store(
            super::colorref_from_rgb(red, green, blue),
            Ordering::Relaxed,
        );
    }

    pub(super) fn install(hwnd_raw: isize) -> Result<(), String> {
        let hwnd = hwnd_raw as HWND;
        let installed = unsafe { SetWindowSubclass(hwnd, Some(subclass_proc), SUBCLASS_ID, 0) };
        if installed == 0 {
            return Err(format!(
                "无法安装窗口缩放保护：{}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(())
    }

    unsafe fn sync_webview_bounds(hwnd: HWND) {
        let mut bounds = RECT::default();
        if unsafe { GetClientRect(hwnd, &mut bounds) } == 0 {
            return;
        }

        let webview = unsafe {
            FindWindowExW(
                hwnd,
                std::ptr::null_mut(),
                WRY_WEBVIEW_CLASS.as_ptr(),
                std::ptr::null(),
            )
        };
        if webview.is_null() {
            return;
        }

        unsafe {
            SetWindowPos(
                webview,
                std::ptr::null_mut(),
                0,
                0,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SWP_NOACTIVATE | SWP_NOZORDER,
            );
        }
    }

    unsafe extern "system" fn subclass_proc(
        hwnd: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _subclass_id: usize,
        _reference_data: usize,
    ) -> LRESULT {
        match message {
            WM_SIZE if wparam as u32 != SIZE_MINIMIZED => {
                // Wry queues its child HWND resize asynchronously. Finish it synchronously so
                // the host window and WebView2 surface reach the new edge in the same UI turn.
                let result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
                unsafe { sync_webview_bounds(hwnd) };
                result
            }
            WM_ERASEBKGND => {
                let hdc = wparam as HDC;
                let mut bounds = RECT::default();
                if !hdc.is_null() && unsafe { GetClientRect(hwnd, &mut bounds) } != 0 {
                    let brush = unsafe {
                        CreateSolidBrush(PAINT_COLOR.load(Ordering::Relaxed) as COLORREF)
                    };
                    if !brush.is_null() {
                        unsafe {
                            FillRect(hdc, &bounds, brush);
                            DeleteObject(brush as _);
                        }
                    }
                }
                1
            }
            WM_EXITSIZEMOVE => {
                let result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
                unsafe {
                    RedrawWindow(
                        hwnd,
                        std::ptr::null(),
                        std::ptr::null_mut(),
                        RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN,
                    );
                }
                result
            }
            _ => unsafe { DefSubclassProc(hwnd, message, wparam, lparam) },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::colorref_from_rgb;

    #[test]
    fn packs_colorref_in_bgr_order() {
        assert_eq!(colorref_from_rgb(247, 245, 241), 0x00F1_F5F7);
        assert_eq!(colorref_from_rgb(23, 26, 24), 0x0018_1A17);
    }
}
