using System.Collections.Generic;
using UnityEngine;

namespace EasyScroller.Samples
{
    [CreateAssetMenu(fileName = "ExampleDataHolder", menuName = "EasyScroller/Samples/Example Data Holder")]
    public class ExampleDataHolder : ScriptableObject
    {
        public List<Sprite> sprite_list;
    }
}
