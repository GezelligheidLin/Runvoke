//! Reduces the trailing-edge gap caused by WebView2's windowed hosting on Windows.

use tauri::{Runtime, WebviewWindow};

#[cfg(any(windows, test))]
const fn colorref_from_rgb(red: u8, green: u8, blue: u8) -> u32 {
    (red as u32) | ((green as u32) << 8) | ((blue as u32) << 16)
}

#[cfg(any(windows, test))]
fn normalize_notification_regions(regions: Vec<(i32, i32, i32, i32)>) -> Vec<(i32, i32, i32, i32)> {
    regions
        .into_iter()
        .filter(|(left, top, right, bottom)| right > left && bottom > top)
        .collect()
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

pub fn redraw<R: Runtime>(window: &WebviewWindow<R>) -> Result<(), String> {
    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取窗口句柄：{error}"))?;
        imp::redraw(hwnd.0 as isize)
    }

    #[cfg(not(windows))]
    {
        let _ = window;
        Ok(())
    }
}

pub fn set_outer_bounds<R: Runtime>(
    window: &WebviewWindow<R>,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取窗口句柄：{error}"))?;
        imp::set_outer_bounds(hwnd.0 as isize, x, y, width, height)
    }

    #[cfg(not(windows))]
    {
        let _ = (window, x, y, width, height);
        Ok(())
    }
}

pub fn set_notification_window_regions<R: Runtime>(
    window: &WebviewWindow<R>,
    regions: Vec<(i32, i32, i32, i32)>,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取通知窗口句柄：{error}"))?;
        let hwnd_raw = hwnd.0 as isize;
        imp::configure_notification_window(hwnd_raw)?;
        imp::set_notification_window_regions(hwnd_raw, normalize_notification_regions(regions))
    }

    #[cfg(not(windows))]
    {
        let _ = (window, regions);
        Ok(())
    }
}

pub fn configure_notification_window<R: Runtime>(window: &WebviewWindow<R>) -> Result<(), String> {
    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取通知窗口句柄：{error}"))?;
        imp::configure_notification_window(hwnd.0 as isize)
    }

    #[cfg(not(windows))]
    {
        let _ = window;
        Ok(())
    }
}

pub fn show_notification_window<R: Runtime>(window: &WebviewWindow<R>) -> Result<(), String> {
    #[cfg(windows)]
    {
        let hwnd = window
            .hwnd()
            .map_err(|error| format!("无法获取通知窗口句柄：{error}"))?;
        imp::configure_notification_window(hwnd.0 as isize)?;
        imp::show_notification_window(hwnd.0 as isize)
    }

    #[cfg(not(windows))]
    {
        window
            .show()
            .map_err(|error| format!("无法显示通知窗口：{error}"))
    }
}

#[cfg(windows)]
mod imp {
    use std::sync::atomic::{AtomicIsize, AtomicU32, Ordering};

    use windows_sys::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        CombineRgn, CreateRectRgn, CreateSolidBrush, DeleteObject, FillRect, RedrawWindow,
        SetWindowRgn, HDC, RDW_ALLCHILDREN, RDW_INVALIDATE, RDW_UPDATENOW, RGN_ERROR, RGN_OR,
    };
    use windows_sys::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        FindWindowExW, GetClientRect, GetWindowLongPtrW, SetWindowLongPtrW, SetWindowPos,
        GWL_EXSTYLE, GWL_STYLE, HWND_TOPMOST, SIZE_MINIMIZED, STYLESTRUCT, SWP_FRAMECHANGED,
        SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW, WM_ERASEBKGND,
        WM_EXITSIZEMOVE, WM_NCACTIVATE, WM_NCCALCSIZE, WM_NCDESTROY, WM_NCPAINT, WM_SIZE,
        WM_STYLECHANGING, WS_CAPTION, WS_EX_APPWINDOW, WS_EX_CLIENTEDGE, WS_EX_DLGMODALFRAME,
        WS_EX_NOACTIVATE, WS_EX_STATICEDGE, WS_EX_TOOLWINDOW, WS_EX_WINDOWEDGE, WS_MAXIMIZEBOX,
        WS_MINIMIZEBOX, WS_POPUP, WS_SYSMENU, WS_THICKFRAME,
    };

    const SUBCLASS_ID: usize = 0x5255_4E56;
    const NOTIFICATION_SUBCLASS_ID: usize = 0x4E4F_5449;
    const FORBIDDEN_NOTIFICATION_STYLE: u32 =
        WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
    const FORBIDDEN_NOTIFICATION_EX_STYLE: u32 = WS_EX_APPWINDOW
        | WS_EX_WINDOWEDGE
        | WS_EX_CLIENTEDGE
        | WS_EX_DLGMODALFRAME
        | WS_EX_STATICEDGE;
    const WRY_WEBVIEW_CLASS: [u16; 12] = [87, 82, 89, 95, 87, 69, 66, 86, 73, 69, 87, 0];
    static PAINT_COLOR: AtomicU32 = AtomicU32::new(super::colorref_from_rgb(247, 245, 241));
    static NOTIFICATION_SUBCLASSED_HWND: AtomicIsize = AtomicIsize::new(0);

    fn hardened_notification_style(style: u32) -> u32 {
        (style & !FORBIDDEN_NOTIFICATION_STYLE) | WS_POPUP
    }

    fn hardened_notification_ex_style(style: u32) -> u32 {
        (style & !FORBIDDEN_NOTIFICATION_EX_STYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
    }

    fn ensure_notification_subclass(hwnd: HWND) -> Result<(), String> {
        let hwnd_raw = hwnd as isize;
        if NOTIFICATION_SUBCLASSED_HWND.load(Ordering::Acquire) == hwnd_raw {
            return Ok(());
        }
        let installed = unsafe {
            SetWindowSubclass(
                hwnd,
                Some(notification_subclass_proc),
                NOTIFICATION_SUBCLASS_ID,
                0,
            )
        };
        if installed == 0 {
            return Err(format!(
                "无法安装通知窗口样式保护：{}",
                std::io::Error::last_os_error()
            ));
        }
        NOTIFICATION_SUBCLASSED_HWND.store(hwnd_raw, Ordering::Release);
        Ok(())
    }

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

    pub(super) fn set_notification_window_regions(
        hwnd_raw: isize,
        regions: Vec<(i32, i32, i32, i32)>,
    ) -> Result<(), String> {
        let combined = unsafe { CreateRectRgn(0, 0, 0, 0) };
        if combined.is_null() {
            return Err(format!(
                "无法创建通知窗口区域：{}",
                std::io::Error::last_os_error()
            ));
        }

        for (left, top, right, bottom) in regions {
            let region = unsafe { CreateRectRgn(left, top, right, bottom) };
            if region.is_null() {
                unsafe { DeleteObject(combined as _) };
                return Err(format!(
                    "无法创建通知卡片区域：{}",
                    std::io::Error::last_os_error()
                ));
            }
            let result = unsafe { CombineRgn(combined, combined, region, RGN_OR) };
            unsafe { DeleteObject(region as _) };
            if result == RGN_ERROR {
                unsafe { DeleteObject(combined as _) };
                return Err(format!(
                    "无法合并通知窗口区域：{}",
                    std::io::Error::last_os_error()
                ));
            }
        }

        // Region clipping takes effect immediately. Asking Windows to redraw
        // synchronously here can clear the transparent surface one frame
        // before WebView2 paints a stack transition, exposing the window
        // underneath. Vue's animation supplies the required repaint.
        let applied = unsafe { SetWindowRgn(hwnd_raw as HWND, combined, 0) };
        if applied == 0 {
            unsafe { DeleteObject(combined as _) };
            return Err(format!(
                "无法裁剪通知窗口区域：{}",
                std::io::Error::last_os_error()
            ));
        }
        // SetWindowRgn owns `combined` after success. Deleting it here would invalidate the HWND.
        Ok(())
    }

    pub(super) fn configure_notification_window(hwnd_raw: isize) -> Result<(), String> {
        let hwnd = hwnd_raw as HWND;
        ensure_notification_subclass(hwnd)?;
        let current_style = unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) } as u32;
        let desired_style = hardened_notification_style(current_style);
        let current_ex_style = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } as u32;
        let desired_ex_style = hardened_notification_ex_style(current_ex_style);
        let styles_changed = current_style != desired_style || current_ex_style != desired_ex_style;
        if styles_changed {
            unsafe {
                SetWindowLongPtrW(hwnd, GWL_STYLE, desired_style as isize);
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, desired_ex_style as isize);
            }
        }

        let actual_style = unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) } as u32;
        let actual_ex_style = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } as u32;
        if actual_style & FORBIDDEN_NOTIFICATION_STYLE != 0
            || actual_style & WS_POPUP == 0
            || actual_ex_style & FORBIDDEN_NOTIFICATION_EX_STYLE != 0
            || actual_ex_style & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
                != WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        {
            return Err("无法应用通知窗口的无边框工具窗口样式".into());
        }

        if styles_changed {
            let updated = unsafe {
                SetWindowPos(
                    hwnd,
                    std::ptr::null_mut(),
                    0,
                    0,
                    0,
                    0,
                    SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER,
                )
            };
            if updated == 0 {
                return Err(format!(
                    "无法刷新通知窗口样式：{}",
                    std::io::Error::last_os_error()
                ));
            }
        }
        Ok(())
    }

    pub(super) fn show_notification_window(hwnd_raw: isize) -> Result<(), String> {
        let shown = unsafe {
            SetWindowPos(
                hwnd_raw as HWND,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
            )
        };
        if shown == 0 {
            return Err(format!(
                "无法置顶通知窗口：{}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(())
    }

    pub(super) fn redraw(hwnd_raw: isize) -> Result<(), String> {
        let redrawn = unsafe {
            RedrawWindow(
                hwnd_raw as HWND,
                std::ptr::null(),
                std::ptr::null_mut(),
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN,
            )
        };
        if redrawn == 0 {
            return Err(format!("无法重绘窗口：{}", std::io::Error::last_os_error()));
        }
        Ok(())
    }

    pub(super) fn set_outer_bounds(
        hwnd_raw: isize,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
    ) -> Result<(), String> {
        let positioned = unsafe {
            SetWindowPos(
                hwnd_raw as HWND,
                std::ptr::null_mut(),
                x,
                y,
                width,
                height,
                SWP_NOACTIVATE | SWP_NOZORDER,
            )
        };
        if positioned == 0 {
            return Err(format!(
                "无法调整窗口边界：{}",
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

    unsafe extern "system" fn notification_subclass_proc(
        hwnd: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _subclass_id: usize,
        _reference_data: usize,
    ) -> LRESULT {
        match message {
            WM_STYLECHANGING => {
                // Let downstream handlers inspect the request first, then make
                // our style the final value consumed by SetWindowLongPtrW.
                let result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
                if lparam != 0 {
                    let style = unsafe { &mut *(lparam as *mut STYLESTRUCT) };
                    match wparam as i32 {
                        GWL_STYLE => {
                            style.styleNew = hardened_notification_style(style.styleNew);
                        }
                        GWL_EXSTYLE => {
                            style.styleNew = hardened_notification_ex_style(style.styleNew);
                        }
                        _ => {}
                    }
                }
                result
            }
            // Even if framework code requests a frame refresh, the auxiliary
            // window always uses its full bounds as client content and never
            // paints a native caption or activation frame.
            WM_NCCALCSIZE | WM_NCPAINT => 0,
            WM_NCACTIVATE => 1,
            WM_NCDESTROY => {
                NOTIFICATION_SUBCLASSED_HWND.store(0, Ordering::Release);
                unsafe { DefSubclassProc(hwnd, message, wparam, lparam) }
            }
            _ => unsafe { DefSubclassProc(hwnd, message, wparam, lparam) },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{colorref_from_rgb, normalize_notification_regions};

    #[test]
    fn packs_colorref_in_bgr_order() {
        assert_eq!(colorref_from_rgb(247, 245, 241), 0x00F1_F5F7);
        assert_eq!(colorref_from_rgb(23, 26, 24), 0x0018_1A17);
    }

    #[test]
    fn drops_empty_or_inverted_notification_regions() {
        assert_eq!(
            normalize_notification_regions(vec![
                (10, 8, 370, 122),
                (10, 8, 10, 122),
                (10, 8, 9, 122),
                (10, 8, 370, 8),
            ]),
            vec![(10, 8, 370, 122)]
        );
    }
}
