-- CaptureView - FireDog's capture_view: snap a camera and show the frame in the Run page's
-- snapshot box (e.g. the serial-number label, just before the serial prompt).
-- args (optional): camera (default "main_camera"), label (caption).
local a = args or {}
local path = machine.camera.snap(a.camera or "main_camera")
machine.ui.showImage(path, a.label or "")
