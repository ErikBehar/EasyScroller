using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ExampleDataHolder", menuName = "Scriptable Objects/ExampleDataHolder")]
public class ExampleDataHolder : ScriptableObject
{
    public List<Sprite> sprite_list;
}
