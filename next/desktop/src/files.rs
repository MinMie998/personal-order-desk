use tauri_plugin_dialog::DialogExt;
#[tauri::command]
pub async fn choose_folder(app:tauri::AppHandle)->Result<Option<String>,String>{
 tauri::async_runtime::spawn_blocking(move||app.dialog().file().set_title("选择文件夹").blocking_pick_folder().map(|p|p.into_path().map(|v|v.to_string_lossy().into_owned()).map_err(|_|"所选路径无效".to_string())).transpose()).await.map_err(|_|"文件夹选择未完成")?
}
#[tauri::command]
pub async fn save_export(app:tauri::AppHandle,name:String,content:String)->Result<Option<String>,String>{
 if content.len()>50*1024*1024{return Err("导出内容过大，请缩小筛选范围".into())}
 if name.contains(['/', '\\'])||!(name.ends_with(".csv")||name.ends_with(".deskkey")){return Err("导出文件名无效".into())}
 tauri::async_runtime::spawn_blocking(move||{
  let dialog=app.dialog().file().set_title("保存导出文件").set_file_name(&name);let dialog=if name.ends_with(".deskkey"){dialog.add_filter("账本恢复密钥",&["deskkey"])}else{dialog.add_filter("CSV表格",&["csv"])};
  let Some(file)=dialog.blocking_save_file()else{return Ok(None)};
  let path=file.into_path().map_err(|_|"所选保存位置无效")?;
  std::fs::write(&path,content.as_bytes()).map_err(|_|"无法写入导出文件，请检查权限和磁盘空间")?;
  Ok(Some(path.to_string_lossy().into_owned()))
 }).await.map_err(|_|"导出未完成")?
}
