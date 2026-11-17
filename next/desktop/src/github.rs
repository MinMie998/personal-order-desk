const LOGIN_URL:&str="https://github.com/login/device";
#[tauri::command]
pub async fn github_auth(action:String)->Result<serde_json::Value,String>{
 tauri::async_runtime::spawn_blocking(move||desk_sync::auth::execute(&action)).await.map_err(|_|"登录操作未完成")?
}
#[tauri::command]
pub fn open_github_login()->Result<(),String>{
    open_fixed_url(LOGIN_URL)
}
pub fn open_fixed_url(url:&str)->Result<(),String>{
    use windows_sys::Win32::UI::{Shell::ShellExecuteW,WindowsAndMessaging::SW_SHOWNORMAL};
    let verb:Vec<u16>="open".encode_utf16().chain(Some(0)).collect();
    let url:Vec<u16>=url.encode_utf16().chain(Some(0)).collect();
    let result=unsafe{ShellExecuteW(std::ptr::null_mut(),verb.as_ptr(),url.as_ptr(),std::ptr::null(),std::ptr::null(),SW_SHOWNORMAL)};
    if result as isize<=32{Err("无法打开默认浏览器，请手动输入界面上的地址".into())}else{Ok(())}
}
