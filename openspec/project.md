# Valheim Villages - Total Conversion Mod

## Project Purpose

This project is a total conversion mod for Valheim that transforms core gameplay mechanics. The mod focuses on creating a village-building and management experience with new systems, mechanics, and gameplay patterns.

## Technology Stack

- **Language**: C# (.NET Framework)
- **Modding Framework**: BepInEx 5.x
- **Patching Library**: Harmony (for runtime code patching)
- **Game Engine**: Unity (Valheim's underlying engine)
- **Version Control**: Git

## Development Environment

- **Platforms**: Development is performed on macOS or Linux environments
- **Runtime**: Mono is required to build and run the assemblies on non-Windows platforms
- **Build System**: Assemblies must be compatible with Mono runtime for cross-platform development

## Development Principles

### 1. Modular Design

- Features are developed as independent, composable modules
- Each module can be evaluated, tested, and understood in isolation
- Modules interact through well-defined interfaces

### 2. Atomic Features

- Each feature represents a single, discrete capability
- Features are independently implementable and testable
- No feature should require multiple other features to function

### 3. Code Modification Over Addition

- Prioritize modifying existing game code and mod code over adding new layers
- Refactor existing functions to be correct rather than adding workarounds
- Avoid code bloat and unnecessary abstraction

### 4. Discrete Scope

- Changes are narrow, focused, and well-defined
- Each change addresses a single problem or adds a single capability
- Broad changes are broken down into smaller, atomic tasks

## Valheim Game Context

### Core Game Mechanics

Valheim is a survival-crafting game with:

- **Building System**: Players can construct buildings using various materials
- **Crafting System**: Items are crafted at workbenches and forges
- **Combat System**: Melee and ranged combat with various weapons
- **Exploration**: Procedurally generated world with different biomes
- **Progression**: Technology tree unlocked through discovery and boss defeats

### Modding Ecosystem

- **BepInEx**: The standard modding framework for Valheim
- **Harmony**: Runtime patching library used to modify game code
- **Unity Components**: Game objects use Unity's component system
- **ZNet**: Networking layer for multiplayer functionality

### UI and GameObject Practices

- **Asset Reuse**: Inherit, extend, or clone existing in-game UI elements and GameObjects rather than recreating them from scratch
- **Visual Consistency**: Leveraging existing game assets ensures mod elements appear native and maintain visual consistency with the base game
- **Design Efficiency**: Reusing existing assets saves significant UI design effort while ensuring compatibility with game systems
- **Unity Prefabs**: Prefer modifying existing Unity prefabs and components over creating entirely new ones

### Relevant Game Systems

When developing features, consider interactions with:

- Building placement and construction
- Item crafting and resource management
- NPC behavior and AI (if applicable)
- World generation and terrain modification
- Player progression and skill systems
- Multiplayer synchronization

## Project Structure

```
valheim_villages/
├── openspec/              # OpenSpec framework files
│   ├── AGENTS.md          # AI agent instructions
│   ├── project.md         # This file
│   ├── specs/             # System specifications
│   └── changes/           # Active change proposals
└── [source code]          # Mod source code (to be created)
```

## Development Workflow

1. **Spec Creation**: Define atomic features in `openspec/specs/` with requirements and scenarios
2. **Change Proposal**: Create proposals in `openspec/changes/` that identify existing code to modify
3. **Implementation**: Modify existing code to meet requirements, following agent instructions
4. **Spec Updates**: Update specs to reflect new system state after implementation
5. **Review**: Ensure changes are atomic, modify existing code, and maintain modularity

## Success Criteria

A feature is considered complete when:

- It is atomic and independently evaluable
- It modifies existing code rather than adding unnecessary new code
- It has a corresponding spec that accurately describes its behavior
- It integrates cleanly with existing game systems
- It follows Valheim modding best practices
