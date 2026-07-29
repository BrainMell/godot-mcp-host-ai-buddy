# Godot MCP Project Rules

## 1. Node Script Type Matching
- **Inheritance Check**: A script's base class (e.g., `extends CharacterBody2D`) MUST match the node class it is being attached to.
- **Node2D vs CharacterBody2D**: 
  - A script that inherits from `CharacterBody2D` **cannot** be assigned to a plain `Node2D` node. Doing so will result in the Godot error: `Script inherits from native type 'CharacterBody2D', so it can't be assigned to an object of type: 'Node2D'`.
  - Always verify the class type of the target node (using `get_scene_tree` or `get_node_properties`) before writing or attaching a script.
  - If a script needs to use physics features (like `move_and_slide`), ensure the target node is created as or changed to a `CharacterBody2D` (not `Node2D`).
