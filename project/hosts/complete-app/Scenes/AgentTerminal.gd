extends Control

## GDScript wrapper for GodotXterm Terminal (render-only, no PTY).
## Text is fed from C# via write_text().

@onready var terminal: Terminal = $Terminal

func _ready():
	# Ensure terminal has visible colors
	terminal.add_theme_color_override("foreground_color", Color(0.85, 0.87, 0.92))
	terminal.add_theme_color_override("background_color", Color(0.06, 0.07, 0.1))

func write_text(text: String):
	if terminal:
		terminal.write(text)

func clear_terminal():
	if terminal:
		terminal.clear()
