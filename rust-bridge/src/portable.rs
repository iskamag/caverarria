//! Host-fed resource/save stores. Portable engine code has no operating-system filesystem.
use crate::framework::error::{GameError, GameResult};
use crate::framework::vfs::{OpenOptions, VFile, VMetadata, VFS};
use std::collections::{BTreeMap, BTreeSet};
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

type Store = Arc<Mutex<BTreeMap<String, Vec<u8>>>>;
thread_local! {
    static DATA: Store = Arc::new(Mutex::new(BTreeMap::new()));
    static SAVE: Store = Arc::new(Mutex::new(BTreeMap::new()));
}
static TIME: AtomicU64 = AtomicU64::new(1);
static SAVE_REVISION: AtomicU64 = AtomicU64::new(0);
pub fn revision(save: bool) -> u64 {
    if save {
        SAVE_REVISION.load(Ordering::Relaxed)
    } else {
        0
    }
}
pub fn set_time(seconds: u64) {
    TIME.store(seconds.max(1), Ordering::Relaxed);
}
pub fn timestamp() -> u64 {
    TIME.load(Ordering::Relaxed)
}
fn store(save: bool) -> Store {
    if save {
        SAVE.with(Clone::clone)
    } else {
        DATA.with(Clone::clone)
    }
}
fn key(path: &Path) -> GameResult<String> {
    let path = path
        .to_str()
        .ok_or_else(|| GameError::FilesystemError("Invalid UTF8 path".into()))?;
    let parts = path
        .split('/')
        .filter(|p| !p.is_empty())
        .collect::<Vec<_>>();
    if parts
        .iter()
        .any(|p| *p == ".." || *p == "." || p.contains('\\') || p.contains(':'))
    {
        return Err(GameError::FilesystemError("Invalid virtual path".into()));
    }
    Ok(format!("/{}", parts.join("/").to_lowercase()))
}
pub fn clear() {
    DATA.with(|s| s.lock().unwrap().clear());
    SAVE.with(|s| s.lock().unwrap().clear());
}
pub fn put(path: &str, bytes: &[u8], save: bool) -> GameResult<()> {
    store(save)
        .lock()
        .unwrap()
        .insert(key(Path::new(path))?, bytes.to_vec());
    if save {
        SAVE_REVISION.fetch_add(1, Ordering::Relaxed);
    }
    Ok(())
}
pub fn file_pointer(path: &str, save: bool) -> (*const u8, usize) {
    let Ok(path) = key(Path::new(path)) else {
        return (std::ptr::null(), 0);
    };
    let store = store(save);
    let files = store.lock().unwrap();
    files
        .get(&path)
        .map(|v| (v.as_ptr(), v.len()))
        .unwrap_or((std::ptr::null(), 0))
}
pub fn files(save: bool) -> Vec<String> {
    store(save).lock().unwrap().keys().cloned().collect()
}
pub fn extract_original(bytes: &[u8]) -> GameResult<()> {
    for (path, data) in crate::data::vanilla::VanillaExtractor::extract_memory(bytes)? {
        put(&path, &data, false)?;
    }
    Ok(())
}
pub fn data_fs() -> Box<dyn VFS> {
    Box::new(MemoryFS {
        store: store(false),
        readonly: true,
    })
}
pub fn save_fs() -> Box<dyn VFS> {
    Box::new(MemoryFS {
        store: store(true),
        readonly: false,
    })
}

#[derive(Debug)]
struct MemoryFS {
    store: Store,
    readonly: bool,
}
#[derive(Debug)]
struct MemoryFile {
    store: Store,
    path: String,
    position: usize,
    writable: bool,
}
impl Read for MemoryFile {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        let files = self.store.lock().unwrap();
        let data = &files[&self.path];
        let n = out.len().min(data.len().saturating_sub(self.position));
        out[..n].copy_from_slice(
            &data[self.position.min(data.len())..self.position.min(data.len()) + n],
        );
        self.position += n;
        Ok(n)
    }
}
impl Write for MemoryFile {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if !self.writable {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "Read-only resource",
            ));
        }
        let mut files = self.store.lock().unwrap();
        let data = files.get_mut(&self.path).unwrap();
        data.resize(data.len().max(self.position + bytes.len()), 0);
        data[self.position..self.position + bytes.len()].copy_from_slice(bytes);
        SAVE_REVISION.fetch_add(1, Ordering::Relaxed);
        self.position += bytes.len();
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
impl Seek for MemoryFile {
    fn seek(&mut self, offset: SeekFrom) -> io::Result<u64> {
        let position = match offset {
            SeekFrom::Start(n) => n as i64,
            SeekFrom::Current(n) => self.position as i64 + n,
            SeekFrom::End(n) => self.store.lock().unwrap()[&self.path].len() as i64 + n,
        };
        if position < 0 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "Negative seek"));
        }
        self.position = position as usize;
        Ok(position as u64)
    }
}
struct Metadata {
    dir: bool,
    len: u64,
}
impl VMetadata for Metadata {
    fn is_dir(&self) -> bool {
        self.dir
    }
    fn is_file(&self) -> bool {
        !self.dir
    }
    fn len(&self) -> u64 {
        self.len
    }
}
impl VFS for MemoryFS {
    fn open_options(&self, path: &Path, options: OpenOptions) -> GameResult<Box<dyn VFile>> {
        if self.readonly && (options.write || options.create || options.append || options.truncate)
        {
            return Err(GameError::FilesystemError(
                "Read-only resource store".into(),
            ));
        }
        let path = key(path)?;
        let mut files = self.store.lock().unwrap();
        if !files.contains_key(&path) {
            if options.create {
                files.insert(path.clone(), Vec::new());
            } else {
                return Err(GameError::FilesystemError(format!(
                    "Virtual file missing: {path}"
                )));
            }
        }
        if options.truncate {
            files.get_mut(&path).unwrap().clear();
            SAVE_REVISION.fetch_add(1, Ordering::Relaxed);
        }
        let position = if options.append {
            files[&path].len()
        } else {
            0
        };
        Ok(Box::new(MemoryFile {
            store: self.store.clone(),
            path,
            position,
            writable: options.write || options.append,
        }))
    }
    fn mkdir(&self, _path: &Path) -> GameResult {
        Ok(())
    }
    fn rm(&self, path: &Path) -> GameResult {
        if self.readonly {
            return Err(GameError::FilesystemError(
                "Read-only resource store".into(),
            ));
        }
        if self.store.lock().unwrap().remove(&key(path)?).is_some() {
            SAVE_REVISION.fetch_add(1, Ordering::Relaxed);
        }
        Ok(())
    }
    fn rmrf(&self, path: &Path) -> GameResult {
        if self.readonly {
            return Err(GameError::FilesystemError(
                "Read-only resource store".into(),
            ));
        }
        let prefix = key(path)?;
        let mut files = self.store.lock().unwrap();
        let old = files.len();
        files.retain(|p, _| p != &prefix && !p.starts_with(&(prefix.clone() + "/")));
        if old != files.len() {
            SAVE_REVISION.fetch_add(1, Ordering::Relaxed);
        }
        Ok(())
    }
    fn exists(&self, path: &Path) -> bool {
        self.metadata(path).is_ok()
    }
    fn metadata(&self, path: &Path) -> GameResult<Box<dyn VMetadata>> {
        let path = key(path)?;
        let files = self.store.lock().unwrap();
        if let Some(bytes) = files.get(&path) {
            return Ok(Box::new(Metadata {
                dir: false,
                len: bytes.len() as u64,
            }));
        }
        if path == "/" || files.keys().any(|p| p.starts_with(&(path.clone() + "/"))) {
            return Ok(Box::new(Metadata { dir: true, len: 0 }));
        }
        Err(GameError::FilesystemError(format!(
            "Virtual path missing: {path}"
        )))
    }
    fn read_dir(&self, path: &Path) -> GameResult<Box<dyn Iterator<Item = GameResult<PathBuf>>>> {
        let mut prefix = key(path)?;
        if !prefix.ends_with('/') {
            prefix.push('/');
        }
        let paths = self
            .store
            .lock()
            .unwrap()
            .keys()
            .filter_map(|p| p.strip_prefix(&prefix))
            .map(|p| PathBuf::from(prefix.clone() + p.split('/').next().unwrap()))
            .collect::<BTreeSet<_>>();
        Ok(Box::new(paths.into_iter().map(Ok)))
    }
    fn to_path_buf(&self) -> Option<PathBuf> {
        None
    }
}
