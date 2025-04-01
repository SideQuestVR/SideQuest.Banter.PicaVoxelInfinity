using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel
{
    [UnitTitle("On Voxel Manipulator Active Changed")]
    [UnitShortTitle("On Voxel Manipulator Active Changed")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelManipulatorActiveChanged : EventUnit<bool>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnVoxelManipulatorActiveChanged");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<bool>("Active");
        }

        protected override bool ShouldTrigger(Flow flow, bool data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, bool data)
        {
            flow.SetValue(result, data);
        }
    }
}
