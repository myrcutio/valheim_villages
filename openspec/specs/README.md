# Specifications Directory

This directory contains the source of truth for the current system state. Each subdirectory represents a capability, and each capability has a `spec.md` file that defines its requirements and behavior.

## Directory Structure

```
specs/
├── README.md                    # This file
├── [capability-name]/           # One folder per capability
│   └── spec.md                  # Specification for this capability
└── [another-capability]/
    └── spec.md
```

## Creating a New Spec

### 1. Choose an Atomic Capability

Each spec should describe a single, discrete capability that can be:
- Independently understood
- Independently evaluated
- Independently implemented
- Independently tested

**Good examples:**
- `building-foundation-placement` - Handles placement of building foundations
- `villager-job-assignment` - Assigns jobs to villagers
- `resource-storage-access` - Manages access to resource storage

**Bad examples:**
- `village-system` - Too broad, encompasses many capabilities
- `gameplay-changes` - Not atomic, too vague
- `ui-and-building` - Multiple unrelated capabilities

### 2. Create the Capability Folder

Use kebab-case for folder names:
- `villager-ai-behavior`
- `building-material-requirements`
- `resource-gathering-rates`

### 3. Write the Spec

Each `spec.md` file must contain:

#### Purpose
A brief description of what this capability does and why it exists.

#### Requirements
Requirements use "SHALL" statements to define mandatory behavior. Each requirement should be:
- Specific and testable
- Atomic (one requirement per statement)
- Clear about what the system must do

Format:
```markdown
### Requirement: [Requirement Name]
The system SHALL [specific behavior].
```

#### Scenarios
Scenarios use Given-When-Then format to describe specific use cases. Each scenario should:
- Be concrete and testable
- Cover a specific interaction or behavior
- Include all necessary context

Format:
```markdown
#### Scenario: [Scenario Name]
- GIVEN [initial state/context]
- WHEN [action or condition]
- THEN [expected outcome]
- AND [additional expected outcomes, if any]
```

## Example Spec

```markdown
# building-foundation-placement Specification

## Purpose
Manages the placement and validation of building foundations in the village system. Ensures foundations are placed on valid terrain and meet structural requirements.

## Requirements

### Requirement: Terrain validation
The system SHALL validate that foundation placement locations are on valid, flat terrain.

#### Scenario: Valid flat terrain placement
- GIVEN a player attempts to place a foundation
- WHEN the target location is flat and within buildable area
- THEN the foundation placement is allowed
- AND the foundation is placed at the target location

#### Scenario: Invalid sloped terrain placement
- GIVEN a player attempts to place a foundation
- WHEN the target location has a slope greater than 5 degrees
- THEN the foundation placement is rejected
- AND an error message is displayed to the player

### Requirement: Foundation material requirements
The system SHALL require specific materials for foundation construction based on foundation type.

#### Scenario: Stone foundation material requirement
- GIVEN a player attempts to place a stone foundation
- WHEN the player has at least 10 stone in inventory
- THEN the foundation is placed
- AND 10 stone is consumed from inventory

#### Scenario: Insufficient materials for foundation
- GIVEN a player attempts to place a stone foundation
- WHEN the player has less than 10 stone in inventory
- THEN the foundation placement is rejected
- AND a message indicates insufficient materials
```

## Naming Conventions

- **Folder names**: Use kebab-case (e.g., `villager-job-assignment`)
- **Requirement names**: Use descriptive titles (e.g., "Terrain validation", "Material consumption")
- **Scenario names**: Use descriptive titles that indicate the test case (e.g., "Valid flat terrain placement")

## Spec Maintenance

- **Keep specs current**: Update specs when implementing changes
- **Remove obsolete specs**: Delete specs for capabilities that no longer exist
- **Refactor when needed**: If a spec becomes too broad, split it into multiple atomic specs
- **Link related specs**: Use references in spec text to indicate relationships between capabilities

## Atomic Feature Checklist

Before creating a spec, verify:

- [ ] The capability is a single, discrete feature
- [ ] It can be understood without reading other specs
- [ ] It can be implemented independently
- [ ] It has clear, testable requirements
- [ ] Scenarios cover the main use cases
- [ ] The folder name clearly describes the capability
