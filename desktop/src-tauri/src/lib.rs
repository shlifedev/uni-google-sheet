mod sidecar;

use serde_json::Value;
use sidecar::{Host, RpcError};
use tauri::{AppHandle, Manager, RunEvent, State};

#[tauri::command]
async fn rpc(app: AppHandle, host: State<'_, Host>, method: String, params: Option<Value>) -> Result<Value, RpcError> {
    host.call(&app, &method, params.unwrap_or(Value::Object(Default::default()))).await
}

#[tauri::command]
fn launch_path() -> Option<String> {
    std::env::args().skip(1).find(|arg| !arg.starts_with('-'))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_window_state::Builder::default().build())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_store::Builder::default().build())
        .manage(Host::default())
        .invoke_handler(tauri::generate_handler![rpc, launch_path])
        .build(tauri::generate_context!())
        .expect("error while building the HolySheet app")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                app.state::<Host>().shutdown();
            }
        });
}
