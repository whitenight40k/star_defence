using UnityEngine;

namespace StarDefense
{
    /// <summary>
    /// Display-only glue between <see cref="PlayerToolController"/> and the held weapon meshes.
    ///
    /// It shows whichever weapon the controller has selected and nothing more — damage,
    /// ammo, range and building placement all stay in <see cref="PlayerToolController"/>.
    ///
    /// Note for whoever wires this into a scene: when the animated full-body prefab is in
    /// use, leave <c>PlayerToolController.weaponPivot</c> null. That field drives the
    /// procedural first-person weapon bob, and writing to a socket that an Animator is
    /// already driving makes the two fight each other. The Animator owns the arm motion
    /// here, so <c>weaponPivot</c> is only used by this component to place the meshes.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerToolVisuals : MonoBehaviour
    {
        [Tooltip("Leave empty to find the controller on a parent object.")]
        public PlayerToolController tools;

        [Tooltip("The hand socket the weapon meshes are parented to.")]
        public Transform weaponPivot;

        public GameObject gunVisual;
        public GameObject pickaxeVisual;

        private PlayerTool applied;
        private bool initialised;

        private void Awake()
        {
            if (tools == null)
                tools = GetComponentInParent<PlayerToolController>();
        }

        private void OnEnable()
        {
            Sync(force: true);
        }

        private void Update()
        {
            Sync(force: false);
        }

        private void Sync(bool force)
        {
            PlayerTool want = tools != null ? tools.currentTool : PlayerTool.Gun;

            if (!force && initialised && want == applied)
                return;

            applied = want;
            initialised = true;

            if (gunVisual != null)
                gunVisual.SetActive(want == PlayerTool.Gun);
            if (pickaxeVisual != null)
                pickaxeVisual.SetActive(want == PlayerTool.Pickaxe);
        }
    }
}
