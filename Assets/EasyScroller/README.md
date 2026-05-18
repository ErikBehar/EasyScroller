A quick/easy to setup UGUI scroll list solution.

Created as a test for a UI job and refined further afterwards.

Instructions:
1) In your scene, create a Canvas and a child panel inside it.
2) Add "ScrollerManager" and optionally ScrollerInputHandler MonoBehaviours to the panel.
3) On ScrollerManager, choose whether you'd like (a list of prefabs) or (1 prefab and amount).
4) Assign prefabs in the ScrollerManager fields. Run, it should do its thing =] 

Features:
- Quick and easy to setup
- Horizontal or Vertical scrolling options
- Center snapping (optional)
- Scaling towards edges, center, and centered item boost (optional)
- Visible space and Items can be scaled at runtime and list will auto space (optional)
- Infinite scrolling, ie items wrap around to form a loop
- Finite list with scrollbar support
- Item recycling when in 1 prefab and amount mode 
- Separate Input handling
- Ability to add and remove items at runtime (function calls)
- Item events (currently for centering, and updating recycled items) 
- Hide initially until ready (optional)

Notes:
- Tested last on: Unity 6.3.13f1
- Required Packages: UGUI, New Input System and Optionally TextMeshPro (demos)
- Required in Scene: EventSystem + Input System
- Sprites: Note that the food sprite images were generated with Midjourney, can be used freely.

Any issues, questions or comments, email: invadererik@gmail.com