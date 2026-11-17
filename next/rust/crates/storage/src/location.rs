use std::path::{Path,PathBuf};
pub fn configured(default:&Path)->Result<PathBuf,String>{
 let pointer=default.join("storage-location.json");if !pointer.exists(){return Ok(default.to_path_buf())}
 let value:serde_json::Value=serde_json::from_slice(&std::fs::read(&pointer).map_err(|_|"无法读取数据目录登记")?).map_err(|_|"数据目录登记损坏，未切换到空目录")?;
 let path=PathBuf::from(value["directory"].as_str().ok_or("数据目录登记不完整")?);
 if !path.is_absolute()||!path.join("workspace.json").is_file()||!path.join("ledger.db").is_file(){return Err("已登记的数据目录或账本缺失，请重新连接原目录，未新建空库".into())}Ok(path)
}
pub fn register(default:&Path,target:&Path)->Result<(),String>{
 replace_json(&default.join("storage-location.json"),&serde_json::json!({"format":1,"directory":target.to_string_lossy()}))
}
pub fn replace_json(path:&Path,value:&serde_json::Value)->Result<(),String>{
 let default=path.parent().ok_or("登记路径无效")?;
 std::fs::create_dir_all(default).map_err(|_|"无法保存数据目录登记")?;
 let temporary=default.join(format!("location-{}.tmp",uuid::Uuid::new_v4()));
 use std::io::Write;
 let body=value.to_string();
 let mut file=std::fs::OpenOptions::new().create_new(true).write(true).open(&temporary).map_err(|_|"无法保存数据目录登记")?;
 file.write_all(body.as_bytes()).and_then(|_|file.sync_all()).map_err(|_|"数据目录登记保存失败")?;drop(file);
 #[cfg(windows)]{
  use std::os::windows::ffi::OsStrExt;
  use windows_sys::Win32::Storage::FileSystem::{MoveFileExW,MOVEFILE_REPLACE_EXISTING,MOVEFILE_WRITE_THROUGH};
  let source:Vec<u16>=temporary.as_os_str().encode_wide().chain(Some(0)).collect();let dest:Vec<u16>=path.as_os_str().encode_wide().chain(Some(0)).collect();
  if unsafe{MoveFileExW(source.as_ptr(),dest.as_ptr(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)}==0{return Err("数据目录登记更新失败，原目录仍保留".into())}
 }
 #[cfg(not(windows))]std::fs::rename(temporary,path).map_err(|_|"数据目录登记更新失败")?;
 Ok(())
}
