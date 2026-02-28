extends Control

## GDScript wrapper for GodotXterm Terminal + PTY.
## Instantiated from C# HudController for each agent.

@onready var terminal: Terminal = $Terminal
@onready var pty: PTY = $PTY

var _forked := false

func fork_pi(cwd: String, api_key: String) -> int:
	if _forked:
		return OK

	# Set env vars
	pty.env["ZAI_API_KEY"] = api_key
	pty.env["COLORTERM"] = "truecolor"
	pty.env["TERM"] = "xterm-256color"
	pty.use_os_env = true

	var args := PackedStringArray([
		"--mode", "text",
		"--no-session",
		"--provider", "zai",
		"--model", "glm-4.7",
		"-p", "Explore the current directory, read key files, and suggest improvements."
	])

	var result = pty.fork("pi", args, cwd, 120, 24)
	_forked = (result == OK)
	return result

func kill_process():
	if _forked:
		pty.kill(9)
		_forked = false
