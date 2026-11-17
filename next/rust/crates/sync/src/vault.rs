use std::{fs::{self,File,OpenOptions},io::Write,path::PathBuf};
use serde::{Serialize,Deserialize};
use std::os::windows::ffi::OsStrExt;
use windows_sys::Win32::Storage::FileSystem::{MoveFileExW,MOVEFILE_REPLACE_EXISTING,MOVEFILE_WRITE_THROUGH};
use fs2::FileExt;
use windows_sys::Win32::{Foundation::LocalFree,Security::Cryptography::*};
#[derive(Serialize,Deserialize)] pub struct Record{pub instance:String,pub token:String}
fn root()->Result<PathBuf,String>{let root=std::env::var_os("LOCALAPPDATA").ok_or("缺少本机用户数据目录")?;Ok(PathBuf::from(root).join("PersonalOrderDesk.R0"))}
pub fn lock()->Result<File,String>{let dir=root()?;fs::create_dir_all(&dir).map_err(|e|e.to_string())?;let file=OpenOptions::new().create(true).truncate(false).read(true).write(true).open(dir.join("service.lock")).map_err(|e|e.to_string())?;file.lock_exclusive().map_err(|e|e.to_string())?;Ok(file)}
fn crypt(bytes:&[u8],protect:bool)->Result<Vec<u8>,String>{
 let input=CRYPT_INTEGER_BLOB{cbData:u32::try_from(bytes.len()).map_err(|_|"控制凭据过大")?,pbData:bytes.as_ptr() as *mut u8};
 let mut output:CRYPT_INTEGER_BLOB=unsafe{std::mem::zeroed()};
 let okay=unsafe{if protect{CryptProtectData(&input,std::ptr::null(),std::ptr::null(),std::ptr::null(),std::ptr::null(),CRYPTPROTECT_UI_FORBIDDEN,&mut output)}else{CryptUnprotectData(&input,std::ptr::null_mut(),std::ptr::null(),std::ptr::null(),std::ptr::null(),CRYPTPROTECT_UI_FORBIDDEN,&mut output)}};
 if okay==0{return Err("控制凭据无法由当前Windows用户解密，未接管后台".into())}
 let result=unsafe{std::slice::from_raw_parts(output.pbData,output.cbData as usize).to_vec()};unsafe{LocalFree(output.pbData as _);}Ok(result)
}
pub fn read()->Result<Option<Record>,String>{let path=root()?.join("service.bin");if !path.exists(){return Ok(None)}let bytes=fs::read(path).map_err(|e|e.to_string())?;if bytes.len()>65536{return Err("控制凭据格式无效".into())}serde_json::from_slice(&crypt(&bytes,false)?).map(Some).map_err(|_|"控制凭据格式无效".into())}
pub fn save(record:&Record)->Result<(),String>{let dir=root()?;fs::create_dir_all(&dir).map_err(|e|e.to_string())?;let bytes=crypt(&serde_json::to_vec(record).map_err(|e|e.to_string())?,true)?;let temporary=dir.join(format!("{}.tmp",uuid::Uuid::new_v4()));let mut file=OpenOptions::new().create_new(true).write(true).open(&temporary).map_err(|e|e.to_string())?;file.write_all(&bytes).and_then(|_|file.sync_all()).map_err(|e|e.to_string())?;drop(file);let dest=dir.join("service.bin");let source:Vec<u16>=temporary.as_os_str().encode_wide().chain(Some(0)).collect();let target:Vec<u16>=dest.as_os_str().encode_wide().chain(Some(0)).collect();if unsafe{MoveFileExW(source.as_ptr(),target.as_ptr(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)}==0{Err("无法原子保存控制凭据".into())}else{Ok(())}}
pub fn clear()->Result<(),String>{let path=root()?.join("service.bin");if path.exists(){fs::remove_file(path).map_err(|e|e.to_string())?}Ok(())}
// OAuth credentials never cross IPC. Only DPAPI ciphertext is written to disk.
pub fn read_auth()->Result<Option<Vec<u8>>,String>{let path=root()?.join("github-auth.bin");if !path.exists(){return Ok(None)}let bytes=fs::read(path).map_err(|_|"无法读取本机登录凭据")?;if bytes.len()>65536{return Err("本机登录凭据格式无效".into())}crypt(&bytes,false).map(Some)}
pub fn save_auth(bytes:&[u8])->Result<(),String>{
 let dir=root()?;fs::create_dir_all(&dir).map_err(|_|"无法创建本机凭据目录")?;
 let encrypted=crypt(bytes,true)?;let temporary=dir.join(format!("auth-{}.tmp",uuid::Uuid::new_v4()));
 let mut file=OpenOptions::new().create_new(true).write(true).open(&temporary).map_err(|_|"无法保存本机登录凭据")?;
 file.write_all(&encrypted).and_then(|_|file.sync_all()).map_err(|_|"无法写入本机登录凭据")?;drop(file);
 let dest=dir.join("github-auth.bin");let source:Vec<u16>=temporary.as_os_str().encode_wide().chain(Some(0)).collect();let target:Vec<u16>=dest.as_os_str().encode_wide().chain(Some(0)).collect();
 if unsafe{MoveFileExW(source.as_ptr(),target.as_ptr(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)}==0{let _=fs::remove_file(temporary);return Err("无法原子更新登录凭据，请重新授权".into())}Ok(())
}
pub fn clear_auth()->Result<(),String>{let path=root()?.join("github-auth.bin");if path.exists(){fs::remove_file(path).map_err(|_|"无法移除本机登录凭据")?}Ok(())}
#[cfg(test)]mod tests{use super::*;#[test]fn dpapi_roundtrip_and_tamper(){let token=uuid::Uuid::new_v4().to_string();let encrypted=crypt(token.as_bytes(),true).unwrap();assert!(!encrypted.windows(token.len()).any(|w|w==token.as_bytes()));assert_eq!(crypt(&encrypted,false).unwrap(),token.as_bytes());let mut bad=encrypted;let last=bad.len()-1;bad[last]^=1;assert!(crypt(&bad,false).is_err());}}

pub fn seal(bytes:&[u8])->Result<Vec<u8>,String>{crypt(bytes,true)}
pub fn unseal(bytes:&[u8])->Result<Vec<u8>,String>{crypt(bytes,false)}
