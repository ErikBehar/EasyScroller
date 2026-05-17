A quick/easy to setup UGUI scroll list solution.

Created as a test for a UI job and refined further afterwards.

Instructions:
1) In your scene, create a Canvas and a child panel inside it.
2) Add "ScrollerManager" and optionally ScrollerInputHandler monobehaviours to the panel.
3) On ScrollerManager, choose whether you'd like (a list of prefabs) or (1 prefab and amount).
4) Assign prefabs in the ScrollerManager fields. Run, it should do its thing =] 

Features:
- Quick and easy to setup
- Horizontal and Vertical choice
- Center snapping (optional)
- Scaling towards edges, center, and centered item boost (optional)
- Visible space and Items can be scaled at runtime and list will auto space (optional)
- Infinite scrolling, ie items wrap around to form a loop
- Seperate Input handling
- Ability to add and remove items at runtime (function calls)
- Item events (currently for centering) 
- Hide initially until ready (optional)

Drawbacks:
- Currently does not support a finite list, or a scollbar (could be added with little effort)
- No namespace or asmdef (could be added with little effort)
- Currently not optimized for large lists since it doesn't recycle items (could be added, larger effort)

Notes:
- Tested last on: Unity 6.3.13f1
- Required Packages: UGUI, New Input System and Optionally TextMeshPro (demos)
- Required in Scene: EventSystem + Input System
- Sprites: Note that the food sprite images were generated with Midjourney, can be used freely.

Any issues, questions or comments, email: invadererik@gmail.com
