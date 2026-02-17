# Changes Directory

This directory contains active change proposals. Each change proposal represents a planned modification to the system, broken down into reviewable components.

## Directory Structure

```
changes/
├── README.md                    # This file
├── [change-id]/                 # One folder per change proposal
│   ├── proposal.md              # Change intent and high-level description
│   ├── design.md                # Technical design decisions
│   ├── tasks.md                 # Implementation checklist
│   └── specs/                   # Spec deltas (requirement changes)
│       └── [affected-spec]/
│           └── spec.md          # Spec delta showing changes
└── [another-change-id]/
    └── ...
```

## Creating a Change Proposal

### 1. Generate a Change ID

Use a descriptive kebab-case identifier:
- `add-stone-foundation-validation`
- `modify-villager-pathfinding`
- `fix-resource-storage-bug`

### 2. Create the Change Folder

Create a folder with the change ID in `changes/`.

### 3. Write the Proposal Files

#### proposal.md

Describes the change intent at a high level:

```markdown
# Change Proposal: [Change ID]

## Intent
[What are we trying to achieve? Why is this change needed?]

## Scope
[What is the discrete scope of this change? What is explicitly out of scope?]

## Affected Specs
- [List of specs that will be modified]

## Existing Code to Modify
- [List of existing functions, classes, or files that will be modified]
- [Avoid listing new code to add - focus on what exists that will change]
```

#### design.md

Describes technical decisions:

```markdown
# Design: [Change ID]

## Approach
[How will we implement this change?]

## Code Modifications
[Specific existing code that will be modified, with rationale]

## Alternatives Considered
[Other approaches considered and why they were rejected]

## Implementation Strategy
[Step-by-step approach to modifying existing code]
```

#### tasks.md

Breaks down implementation into atomic tasks:

```markdown
# Tasks: [Change ID]

## Implementation Checklist

- [ ] Task 1: [Modify existing function X to do Y]
- [ ] Task 2: [Update existing class Z to handle W]
- [ ] Task 3: [Refactor existing method A to be correct]
- [ ] Task 4: [Update spec X to reflect changes]
```

**Important**: Tasks should focus on modifying existing code, not adding new code.

#### specs/[affected-spec]/spec.md

Shows the spec delta (changes to requirements):

```markdown
# [Spec Name] - Spec Delta

## Changes

### Requirement: [Requirement Name]
- The system SHALL [old behavior].
+ The system SHALL [new behavior].

#### Scenario: [Scenario Name]
- GIVEN [old context]
+ GIVEN [new/updated context]
- WHEN [action]
- THEN [old outcome]
+ THEN [new outcome]
+ AND [additional outcome]
```

Use `-` for removed lines and `+` for added lines to show the delta clearly.

## Change Proposal Principles

### 1. Identify Existing Code First

Before proposing any change:
- Search for existing functions, classes, or systems that handle related functionality
- Identify what should be modified, not what should be added
- Document existing code locations in `proposal.md`

### 2. Discrete Scope

- Each change should address a single, well-defined problem
- If a change affects multiple unrelated systems, split it into separate proposals
- Scope should be narrow enough to review and implement independently

### 3. Code Modification Focus

- Tasks should primarily involve modifying existing code
- Avoid tasks that add new files, classes, or systems unless absolutely necessary
- Prefer refactoring existing functions over creating new ones

### 4. Spec Deltas

- Show exactly how requirements will change
- Use clear diff format (`-` for removed, `+` for added)
- Update all affected specs, not just the primary one

## Example Change Proposal

```
changes/add-foundation-slope-validation/
├── proposal.md
├── design.md
├── tasks.md
└── specs/
    └── building-foundation-placement/
        └── spec.md
```

**proposal.md:**
```markdown
# Change Proposal: add-foundation-slope-validation

## Intent
Add validation to prevent foundation placement on sloped terrain, improving building stability and visual quality.

## Scope
- Add slope validation to existing foundation placement logic
- Display error message when placement fails due to slope
- Out of scope: Terrain modification, foundation rotation

## Affected Specs
- building-foundation-placement

## Existing Code to Modify
- `BuildingManager.PlaceFoundation()` - Add slope check before placement
- `BuildingManager.ValidatePlacement()` - Add slope validation logic
```

**tasks.md:**
```markdown
# Tasks: add-foundation-slope-validation

- [ ] Modify `BuildingManager.ValidatePlacement()` to check terrain slope
- [ ] Update `BuildingManager.PlaceFoundation()` to call slope validation
- [ ] Modify error message system to include slope validation errors
- [ ] Update building-foundation-placement spec with new requirement
```

## Review Criteria

Before implementing a change proposal, verify:

- [ ] Existing code to modify has been identified
- [ ] Change scope is discrete and well-defined
- [ ] Tasks focus on modifying existing code
- [ ] Spec deltas clearly show requirement changes
- [ ] Design explains why modification is preferred over addition
- [ ] All affected specs are listed and have deltas

## After Implementation

Once a change is implemented:

1. **Update specs**: Apply the spec deltas to the actual spec files
2. **Archive or remove**: Move completed changes to an archive or delete the proposal folder
3. **Document learnings**: Note any deviations from the original proposal in design.md
