# Godot Smoke/Steam Effect on Car Collision

This is a standard effect in Godot, and it's simple to set up. The key idea: don't try to reuse one persistent particle node for every collision. Instead, spawn a small one-shot particle effect scene at the collision point each time a crash happens, let it play once, then free itself.

Here's a solid Godot 4 approach:

## 1. Build a reusable "smoke puff" scene

Create a new scene `SmokePuff.tscn` with a single `GPUParticles2D` node (use `CPUParticles2D` instead if you have many simultaneous collisions and want to save GPU/driver overhead — behavior is nearly identical, just CPU-simulated).

Configure it in the inspector:

- **Emitting**: off (you'll trigger it from code)
- **One Shot**: on
- **Amount**: ~20-40
- **Lifetime**: ~0.6-1.0s
- **Explosiveness**: ~0.8-1.0 (makes all particles burst out near-simultaneously, like an impact, rather than trickling out)
- **Process Material** (a new `ParticleProcessMaterial`):
  - Direction: (0, -1) with Spread ~180° for an omnidirectional puff, or narrower if you want a directional "steam" plume
  - Initial Velocity: moderate, with some randomness
  - Gravity: slight upward or zero (smoke drifts up slowly; for a top-down view you may want zero gravity and just let it expand/fade)
  - Scale: start small, use a Scale curve that grows over lifetime (smoke puffs expand)
  - Color/Alpha: use a Color Ramp / Alpha curve that fades to transparent by end of life — this is what sells the "dissipating smoke" look
  - Damping: add some so particles slow down and drift rather than fly straight

For the texture, either use a soft round gradient sprite (a simple white circle with blurred/feathered edge works great, tinted grey), or layer 2-3 of them for a fluffier look.

## 2. Trigger it on collision

In your car/vehicle script where you detect the collision (`Area2D.body_entered`, `CharacterBody2D` collision, or wherever your simulation resolves crashes):

```gdscript
const SmokePuffScene = preload("res://effects/SmokePuff.tscn")

func _on_collision(collision_position: Vector2) -> void:
    var puff = SmokePuffScene.instantiate()
    get_tree().current_scene.add_child(puff)
    puff.global_position = collision_position
    puff.emitting = true
    puff.finished.connect(puff.queue_free)
```

The `finished` signal fires once a one-shot emitter has emitted all its particles and they've all died — connecting it straight to `queue_free` means the node cleans itself up automatically, so you don't leak nodes across a long-running simulation with many crashes.

## Why not just toggle `emitting` on a single persistent node?

As confirmed by discussions on the [Godot subreddit](https://www.reddit.com/r/godot/comments/1cbi45i/retrigger_a_oneshot_particle_node/), one-shot particle nodes have a known quirk: re-triggering `emitting = true` on the same node in quick succession (e.g., two collisions close together) can fail to restart properly, or it cuts off particles still playing from the previous burst. Spawning a fresh instance per collision (as above) sidesteps this entirely and also lets multiple simultaneous crashes each get their own independent smoke puff.

## Extra touches for a traffic-sim look

- **Layer/z-index**: put the puff scene's z-index above your car sprites so smoke renders on top.
- **Combine effects**: for a more "car crash" feel rather than pure steam, add a second short-lived `GPUParticles2D` for sparks/debris (small bright squares, high initial velocity, short lifetime, gravity on) alongside the smoke puff, plus maybe a quick camera shake or a brief scale-punch on the colliding cars.
- **Performance**: if your simulation can have dozens of collisions per second at scale (e.g., stress-testing traffic flow), consider capping simultaneous puffs or pooling/reusing instances instead of instantiate/free each time, since GPUParticles2D node creation isn't free.

## Source

- [Retrigger a oneshot particle node? — r/godot](https://www.reddit.com/r/godot/comments/1cbi45i/retrigger_a_oneshot_particle_node/)
