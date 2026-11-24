use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde::Serialize;
use serde_json::{Value, json};
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tokio::sync::oneshot;

#[derive(Debug, Clone, Serialize)]
pub struct RpcError {
    pub code: i64,
    pub message: String,
}

impl RpcError {
    pub const HOST_UNAVAILABLE: i64 = -32000;

    fn host(message: impl Into<String>) -> Self {
        Self { code: Self::HOST_UNAVAILABLE, message: message.into() }
    }
}

type Pending = Arc<Mutex<HashMap<u64, oneshot::Sender<Result<Value, RpcError>>>>>;

struct Running {
    child: CommandChild,
    generation: u64,
}

#[derive(Default)]
pub struct Host {
    running: Mutex<Option<Running>>,
    pending: Pending,
    next_id: AtomicU64,
    generation: AtomicU64,
}

impl Host {
    pub async fn call(&self, app: &AppHandle, method: &str, params: Value) -> Result<Value, RpcError> {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        let (sender, receiver) = oneshot::channel();
        self.pending.lock().unwrap().insert(id, sender);

        let mut line = serde_json::to_vec(&json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }))
            .map_err(|error| RpcError::host(error.to_string()))?;
        line.push(b'\n');

        if let Err(error) = self.write(app, &line) {
            self.pending.lock().unwrap().remove(&id);
            return Err(error);
        }

        receiver.await.unwrap_or_else(|_| Err(RpcError::host("The engine process stopped before it answered.")))
    }

    fn write(&self, app: &AppHandle, line: &[u8]) -> Result<(), RpcError> {
        let mut running = self.running.lock().unwrap();
        if running.is_none() {
            *running = Some(self.spawn(app)?);
        }

        let result = running.as_mut().unwrap().child.write(line);
        if let Err(error) = result {
            *running = None;
            return Err(RpcError::host(format!("Could not reach the engine process: {error}")));
        }

        Ok(())
    }

    fn spawn(&self, app: &AppHandle) -> Result<Running, RpcError> {
        let (mut events, child) = app
            .shell()
            .sidecar("holysheet-host")
            .map_err(|error| RpcError::host(format!("The engine (holysheet-host) is missing from the app: {error}")))?
            .set_raw_out(true)
            .spawn()
            .map_err(|error| RpcError::host(format!("Could not start the engine (holysheet-host): {error}")))?;

        let generation = self.generation.fetch_add(1, Ordering::Relaxed) + 1;
        if generation > 1 {
            let _ = app.emit("holysheet://host-restarted", generation);
        }

        let pending = self.pending.clone();
        let app = app.clone();
        tauri::async_runtime::spawn(async move {
            let mut buffer: Vec<u8> = Vec::new();
            while let Some(event) = events.recv().await {
                match event {
                    CommandEvent::Stdout(bytes) => {
                        let mut from = buffer.len();
                        buffer.extend_from_slice(&bytes);
                        while let Some(offset) = buffer[from..].iter().position(|byte| *byte == b'\n') {
                            let end = from + offset;
                            let line: Vec<u8> = buffer.drain(..=end).collect();
                            dispatch(&app, &pending, &line[..line.len() - 1]);
                            from = 0;
                        }
                    }
                    CommandEvent::Stderr(bytes) => {
                        eprint!("[holysheet-host] {}", String::from_utf8_lossy(&bytes));
                    }
                    CommandEvent::Error(message) => eprintln!("[holysheet-host] {message}"),
                    CommandEvent::Terminated(status) => {
                        let host = app.state::<Host>();
                        let mut running = host.running.lock().unwrap();
                        if running.as_ref().is_some_and(|current| current.generation == generation) {
                            *running = None;
                        }
                        drop(running);
                        fail_all(&pending, "The engine process exited.");
                        let _ = app.emit("holysheet://host-exited", status.code);
                        break;
                    }
                    _ => {}
                }
            }
        });

        Ok(Running { child, generation })
    }

    pub fn shutdown(&self) {
        if let Some(running) = self.running.lock().unwrap().take() {
            let _ = running.child.kill();
        }
        fail_all(&self.pending, "The app is closing.");
    }
}

fn dispatch(app: &AppHandle, pending: &Pending, line: &[u8]) {
    let line = line.strip_suffix(b"\r").unwrap_or(line);
    if line.is_empty() {
        return;
    }

    let message: Value = match serde_json::from_slice(line) {
        Ok(value) => value,
        Err(error) => {
            eprintln!("[holysheet-host] not JSON ({error}): {}", String::from_utf8_lossy(line));
            return;
        }
    };

    if message.get("method").is_some() && message.get("id").is_none_or(Value::is_null) {
        let _ = app.emit("holysheet://event", &message);
        return;
    }

    let Some(id) = message.get("id").and_then(Value::as_u64) else {
        eprintln!("[holysheet-host] response without a usable id: {message}");
        return;
    };

    let Some(sender) = pending.lock().unwrap().remove(&id) else {
        return;
    };

    let result = match message.get("error") {
        Some(error) => Err(RpcError {
            code: error.get("code").and_then(Value::as_i64).unwrap_or(-32603),
            message: error.get("message").and_then(Value::as_str).unwrap_or("Unknown error").to_string(),
        }),
        None => Ok(message.get("result").cloned().unwrap_or(Value::Null)),
    };
    let _ = sender.send(result);
}

fn fail_all(pending: &Pending, message: &str) {
    for (_, sender) in pending.lock().unwrap().drain() {
        let _ = sender.send(Err(RpcError::host(message)));
    }
}
