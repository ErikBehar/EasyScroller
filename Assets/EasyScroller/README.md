A quick/easy to setup UGUI scroll list solution.

Created as a test for a UI job and refined further afterwards.

## Setup

1. In your scene, create a Canvas and a child panel inside it.
2. Add `ScrollerManager` and optionally `ScrollerInputHandler` to the panel.
3. On ScrollerManager, choose **Prefab List** or **Single Prefab With Count**.
4. Assign prefabs and run.

## Features

- Horizontal or vertical scrolling
- Chain-based layout: items stay spaced via neighbor springs; scroll moves the visible chain together
- Snapping to the centered item (optional)
- Distance scaling, center boost, and per-item highlight on the centered visual (optional)
- Runtime size measurement with automatic spacing updates (optional)
- Infinite or finite list modes; finite mode supports an optional scrollbar
- Visual recycling in single-prefab mode
- Separate input handling
- Runtime add / remove / reorder; scroll by logical or stable data index
- Item events: centered state, content refresh on pool rebind

## Requirements

- Tested on Unity 6.3.13f1
- Required Packages: UGUI, Input System; TextMeshPro optional (used in samples)
- Scene: EventSystem + Input System UI Input Module

## Notes

- Demo sprites were generated with Midjourney and may be used freely.
- Questions: invadererik@gmail.com
