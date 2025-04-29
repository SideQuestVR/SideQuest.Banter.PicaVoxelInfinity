using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel.VisualScripting
{
    [UnitTitle("On Voxel Manipulator Value Changed")]
    [UnitShortTitle("On Voxel Manipulator Value Changed")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelManipulatorValueChanged : EventUnit<int>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnVoxelManipulatorValueChanged");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<int>("Value");
        }

        protected override bool ShouldTrigger(Flow flow, int data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, int data)
        {
            flow.SetValue(result, data);
        }
    }
}
