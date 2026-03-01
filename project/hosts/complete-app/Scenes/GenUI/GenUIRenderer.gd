extends Control

## GenUI Renderer — renders A2UI surface specs as native Godot Control nodes.
## Called from C# via render_a2ui(agent_id, a2ui_json).
## Emits action_triggered when buttons/actions are clicked.

signal action_triggered(agent_id: String, surface_id: String, action_id: String, component_id: String)

# Component catalog: A2UI type → Godot Control factory
var _component_catalog := {
	"label": _create_label,
	"button": _create_button,
	"text_input": _create_text_input,
	"container": _create_container,
	"progress": _create_progress,
	"checkbox": _create_checkbox,
	"separator": _create_separator,
}

# Track active surfaces: surface_id → root Control
var _surfaces := {}
# Track data models: surface_id → Dictionary
var _data_models := {}
# Track bound controls: surface_id → { json_pointer → Control }
var _bound_controls := {}


func render_a2ui(agent_id: String, a2ui_json: String) -> void:
	var parsed = JSON.parse_string(a2ui_json)
	if parsed == null or not parsed is Dictionary:
		push_warning("GenUIRenderer: invalid A2UI JSON")
		return

	var msg: Dictionary = parsed
	var msg_type: String = msg.get("type", "")
	var surface_id: String = msg.get("surfaceId", "default")

	match msg_type:
		"createSurface":
			_handle_create_surface(agent_id, surface_id, msg)
		"updateComponents":
			_handle_update_components(agent_id, surface_id, msg)
		"updateDataModel":
			_handle_update_data_model(surface_id, msg)
		"deleteSurface":
			_handle_delete_surface(surface_id)
		_:
			# Legacy: treat as createSurface
			_handle_create_surface(agent_id, surface_id, msg)


func _handle_create_surface(agent_id: String, surface_id: String, msg: Dictionary) -> void:
	# Remove existing surface if present
	_handle_delete_surface(surface_id)

	var root := VBoxContainer.new()
	root.name = "Surface_%s" % surface_id
	root.set_meta("surface_id", surface_id)
	root.set_meta("agent_id", agent_id)
	add_child(root)
	_surfaces[surface_id] = root
	_bound_controls[surface_id] = {}

	# Parse data model
	if msg.has("dataModel") and msg["dataModel"] is Dictionary:
		_data_models[surface_id] = msg["dataModel"]

	# Render components
	if msg.has("components") and msg["components"] is Array:
		_render_components(agent_id, surface_id, msg["components"], root)

	# Apply initial data bindings
	_apply_data_bindings(surface_id)


func _handle_update_components(agent_id: String, surface_id: String, msg: Dictionary) -> void:
	if not _surfaces.has(surface_id):
		# No existing surface — create it
		_handle_create_surface(agent_id, surface_id, msg)
		return

	var root: Control = _surfaces[surface_id]
	# Clear existing children and re-render
	for child in root.get_children():
		child.queue_free()
	_bound_controls[surface_id] = {}

	if msg.has("components") and msg["components"] is Array:
		_render_components(agent_id, surface_id, msg["components"], root)

	_apply_data_bindings(surface_id)


func _handle_update_data_model(surface_id: String, msg: Dictionary) -> void:
	if msg.has("dataModel") and msg["dataModel"] is Dictionary:
		if not _data_models.has(surface_id):
			_data_models[surface_id] = {}
		# Merge updates
		for key in msg["dataModel"]:
			_data_models[surface_id][key] = msg["dataModel"][key]
		_apply_data_bindings(surface_id)


func _handle_delete_surface(surface_id: String) -> void:
	if _surfaces.has(surface_id):
		var root: Control = _surfaces[surface_id]
		root.queue_free()
		_surfaces.erase(surface_id)
	_data_models.erase(surface_id)
	_bound_controls.erase(surface_id)


func _render_components(agent_id: String, surface_id: String, components: Array, parent: Control) -> void:
	# Build id→parent map for flat adjacency
	var id_to_control := {}
	id_to_control[""] = parent  # root

	for comp in components:
		if not comp is Dictionary:
			continue

		var comp_type: String = comp.get("type", "label")
		var comp_id: String = comp.get("id", "auto-%d" % id_to_control.size())
		var parent_id: String = comp.get("parentId", "")
		var props: Dictionary = comp.get("props", {})
		var data_binding = comp.get("dataBinding", null)
		var actions: Array = comp.get("actions", [])

		# Create the control
		var control: Control = _create_component(comp_type, comp_id, props)
		if control == null:
			continue

		control.set_meta("component_id", comp_id)

		# Add to parent (flat adjacency)
		var target_parent: Control = id_to_control.get(parent_id, parent)
		target_parent.add_child(control)
		id_to_control[comp_id] = control

		# Wire data binding
		if data_binding != null and data_binding is String:
			_bound_controls[surface_id][data_binding] = control
			_wire_two_way_binding(surface_id, data_binding, control, comp_type)

		# Wire actions
		for action in actions:
			if not action is Dictionary:
				continue
			var action_id: String = action.get("id", "")
			var action_type: String = action.get("type", "click")
			if action_type == "click" and control is Button:
				var btn: Button = control
				# Capture variables for closure
				var _aid := agent_id
				var _sid := surface_id
				var _actid := action_id
				var _cid := comp_id
				btn.pressed.connect(func(): action_triggered.emit(_aid, _sid, _actid, _cid))


func _create_component(comp_type: String, comp_id: String, props: Dictionary) -> Control:
	if _component_catalog.has(comp_type):
		return _component_catalog[comp_type].call(comp_id, props)
	# Unknown type — render as label
	return _create_label(comp_id, props)


# ── Component factories ──

func _create_label(_id: String, props: Dictionary) -> Label:
	var label := Label.new()
	label.text = str(props.get("text", ""))
	if props.has("fontSize"):
		label.add_theme_font_size_override("font_size", int(props["fontSize"]))
	label.autowrap_mode = TextServer.AUTOWRAP_WORD
	return label


func _create_button(_id: String, props: Dictionary) -> Button:
	var button := Button.new()
	button.text = str(props.get("text", props.get("label", "Button")))
	return button


func _create_text_input(_id: String, props: Dictionary) -> LineEdit:
	var input := LineEdit.new()
	input.placeholder_text = str(props.get("placeholder", ""))
	input.text = str(props.get("value", ""))
	return input


func _create_container(_id: String, props: Dictionary) -> BoxContainer:
	var direction: String = str(props.get("direction", "vertical"))
	var container: BoxContainer
	if direction == "horizontal":
		container = HBoxContainer.new()
	else:
		container = VBoxContainer.new()
	if props.has("gap"):
		container.add_theme_constant_override("separation", int(props["gap"]))
	return container


func _create_progress(_id: String, props: Dictionary) -> ProgressBar:
	var bar := ProgressBar.new()
	bar.min_value = float(props.get("min", 0))
	bar.max_value = float(props.get("max", 100))
	bar.value = float(props.get("value", 0))
	return bar


func _create_checkbox(_id: String, props: Dictionary) -> CheckBox:
	var cb := CheckBox.new()
	cb.text = str(props.get("label", ""))
	cb.button_pressed = props.get("checked", false) == true or str(props.get("checked", "false")) == "true"
	return cb


func _create_separator(_id: String, _props: Dictionary) -> HSeparator:
	return HSeparator.new()


# ── Data binding ──

func _apply_data_bindings(surface_id: String) -> void:
	if not _data_models.has(surface_id) or not _bound_controls.has(surface_id):
		return

	var model: Dictionary = _data_models[surface_id]
	var bindings: Dictionary = _bound_controls[surface_id]

	for pointer in bindings:
		var value = _resolve_json_pointer(model, pointer)
		if value == null:
			continue
		var control: Control = bindings[pointer]
		_set_control_value(control, value)


func _wire_two_way_binding(surface_id: String, pointer: String, control: Control, comp_type: String) -> void:
	var _sid := surface_id
	var _ptr := pointer

	if comp_type == "text_input" and control is LineEdit:
		var line_edit: LineEdit = control
		line_edit.text_changed.connect(func(new_text: String):
			_set_data_model_value(_sid, _ptr, new_text)
		)
	elif comp_type == "checkbox" and control is CheckBox:
		var cb: CheckBox = control
		cb.toggled.connect(func(toggled_on: bool):
			_set_data_model_value(_sid, _ptr, toggled_on)
		)


func _set_data_model_value(surface_id: String, pointer: String, value) -> void:
	if not _data_models.has(surface_id):
		_data_models[surface_id] = {}
	# Simple top-level key from pointer (strip leading /)
	var key := pointer.trim_prefix("/")
	_data_models[surface_id][key] = value


func _resolve_json_pointer(model: Dictionary, pointer: String):
	## RFC 6901 JSON Pointer resolution
	if not pointer.begins_with("/"):
		return null
	var parts := pointer.substr(1).split("/")
	var current = model
	for part in parts:
		# Unescape ~1 → / and ~0 → ~
		part = part.replace("~1", "/").replace("~0", "~")
		if current is Dictionary and current.has(part):
			current = current[part]
		else:
			return null
	return current


func _set_control_value(control: Control, value) -> void:
	if control is LineEdit:
		control.text = str(value)
	elif control is Label:
		control.text = str(value)
	elif control is ProgressBar:
		control.value = float(value)
	elif control is CheckBox:
		control.button_pressed = value == true or str(value) == "true"
