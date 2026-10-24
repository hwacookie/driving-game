# Godot Web Rendering & Streaming: Technical Architecture & API Reference

This document provides a technical breakdown of the two primary methods for deploying a Godot-based traffic simulation to web clients. It is intended to serve as a reference for automated agents and developers, focusing on API endpoints, required server configurations, and official documentation sources rather than video tutorials.

---

## 1. Native Web Export (Client-Side Rendering)

This approach compiles the Godot engine and simulation logic into WebAssembly (WASM) and uses the browser's graphics APIs (WebGL 2.0 or WebGPU) to render the simulation locally on the client's machine.

### 1.1 Technical Implementation Details
*   **Compilation:** Godot uses Emscripten to compile C++ engine code to `.wasm`.
*   **Multithreading (Crucial for Traffic AI/Physics):** To utilize threads in the browser, the server hosting the web export must enforce specific HTTP headers to enable `SharedArrayBuffer`. 
    *   **Required Headers:**
        *   `Cross-Origin-Opener-Policy: same-origin`
        *   `Cross-Origin-Embedder-Policy: require-corp`
*   **Browser Interaction:** Godot provides a bridge to execute JavaScript code and evaluate browser states directly from GDScript/C#.

### 1.2 Key APIs and Classes (Godot 4.x)
*   **`JavaScriptBridge`:** The core singleton used to interact with the browser. 
    *   *Usage:* `JavaScriptBridge.eval("window.alert('Simulation loaded')")`
    *   *Callbacks:* `JavaScriptBridge.create_callback()` to map JS events (like resizing or custom UI button presses) back to GDScript functions.
*   **`OS.has_feature("web")`:** Used to conditionally execute code only when the game is running in a browser environment.

### 1.3 Official Reference Sources
*   **Exporting for the Web:** 
    *   URL: `https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_web.html`
    *   *Context:* Details the export process, HTML shell customization, and limitations.
*   **JavaScriptBridge API Documentation:**
    *   URL: `https://docs.godotengine.org/en/stable/classes/class_javascriptbridge.html`
*   **Web Export & Multithreading:**
    *   URL: `https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_web.html#threads`

---

## 2. Server-Side Pixel Streaming (WebRTC)

This approach runs the simulation on a dedicated, GPU-accelerated server. The server captures rendered frames, encodes them, and streams them as video to a lightweight browser client via WebRTC. User inputs are sent back via WebRTC DataChannels.

### 2.1 Technical Implementation Details
*   **Server Mode:** The Godot instance runs on a server. While it can run entirely headless (`--headless`), for pixel streaming, a virtual display (like Xvfb on Linux) or an active GPU context is required to render the frames before encoding.
*   **Frame Capture:** The viewport's texture must be extracted every frame. This is typically passed to a GDExtension (C++) that wraps a hardware encoder (e.g., NVENC for NVIDIA GPUs, or FFmpeg libraries).
*   **WebRTC Signaling:** A signaling server (usually a lightweight Node.js or Python WebSocket server) is required initially to exchange SDP (Session Description Protocol) and ICE candidates between the Godot Server and the Browser Client.
*   **Input Injection:** Input data (JSON) received via the `WebRTCDataChannel` is parsed and injected into Godot's `Input` singleton to drive the simulation.

### 2.2 Key APIs and Classes (Godot 4.x)
*   **Viewport Capture:**
    *   `get_viewport().get_texture().get_image()`: Retrieves the current frame data (Note: Doing this purely in GDScript every frame is slow; it is highly recommended to do this within a C++ GDExtension).
*   **WebRTC Components (Requires `webrtc-native` plugin):**
    *   **`WebRTCPeerConnection`:** Manages the connection to the browser client.
    *   **`WebRTCDataChannel`:** Used for low-latency, bidirectional data transfer (sending simulation telemetry out, receiving keyboard/mouse input in).
    *   **`WebRTCMultiplayerPeer`:** Can be used if treating the stream as a multiplayer session.
*   **Input Injection:**
    *   `Input.parse_input_event(event)`: Injects constructed `InputEventMouseMotion`, `InputEventKey`, etc., into the engine based on data received from the DataChannel.

### 2.3 Official Reference Sources & Example Repositories
*   **Godot WebRTC Documentation:**
    *   URL: `https://docs.godotengine.org/en/stable/tutorials/networking/webrtc.html`
*   **Official WebRTC Native Plugin Repository:**
    *   URL: `https://github.com/godotengine/webrtc-native`
    *   *Context:* The official C++ implementation required to use WebRTC in desktop/server builds.
*   **Godot Signaling Server Example:**
    *   URL: `https://github.com/godotengine/godot-demo-projects/tree/master/networking/webrtc_signaling`
    *   *Context:* Reference implementation for setting up the handshake between the Godot server and web client.

### 2.4 Architectural Recommendation for Pixel Streaming
Building a highly performant Pixel Streaming solution entirely from scratch in GDScript is not feasible due to frame capture and encoding bottlenecks. It is strongly recommended to either:
1.  Develop a custom **GDExtension** in C++ that integrates FFmpeg or NVIDIA Video Codec SDK to grab viewport textures via `RenderingServer` and pass them directly to the WebRTC video track.
2.  Evaluate existing community GDExtensions specifically designed for Godot 4.x WebRTC Video Streaming.