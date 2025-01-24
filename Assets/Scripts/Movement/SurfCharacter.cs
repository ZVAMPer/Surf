﻿using UnityEngine;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;

namespace Fragsurf.Movement
{
    /// <summary>
    /// Easily add a surfable character to the scene,
    /// now modified to use a *client-authoritative* transform.
    /// All movement logic runs locally on the owner client.
    /// The server no longer calculates movement or calls Teleport.
    /// </summary>
    [AddComponentMenu("Fragsurf/Surf Character (Client-Auth)")]
    [RequireComponent(typeof(ClientNetworkTransform))]
    public class SurfCharacter : NetworkBehaviour, ISurfControllable
    {
        public enum ColliderType
        {
            Capsule,
            Box
        }

        [Header("Physics Settings")]
        public Vector3 colliderSize = new Vector3(1f, 2f, 1f);
        [HideInInspector] 
        public ColliderType collisionType { get { return ColliderType.Capsule; } }
        public float weight = 75f;
        public float rigidbodyPushForce = 2f;
        public bool solidCollider = false;

        [Header("View Settings")]
        public Transform viewTransform;
        public Transform playerRotationTransform;

        [Header("Crouching setup")]
        public float crouchingHeightMultiplier = 0.5f;
        public float crouchingSpeed = 10f;
        private float defaultHeight;
        private bool allowCrouch = true;

        [Header("Features")]
        public bool crouchingEnabled = true;
        public bool slidingEnabled = false;
        public bool laddersEnabled = true;
        public bool supportAngledLadders = true;

        [Header("Step offset (can be buggy)")]
        public bool useStepOffset = false;
        public float stepOffset = 0.35f;

        [Header("Movement Config")]
        public MovementConfig movementConfig;

        // Local movement data (no longer a NetworkVariable).
        private MoveData _localMoveData = new MoveData();

        private GameObject _groundObject;
        private Vector3 _baseVelocity;
        private Collider _collider;
        private SurfController _controller = new SurfController();
        private Rigidbody _rb;
        private GameObject _colliderObject;
        private GameObject _cameraWaterCheckObject;
        private CameraWaterCheck _cameraWaterCheck;

        private List<Collider> _triggers = new List<Collider>();
        private int _numberOfTriggers = 0;
        private bool _underwater = false;

        // For storing our start position (used in Reset()).
        private Vector3 _startPosition;

        // This was in your old code but not used much.
        private Vector3 _angles;
        private Vector3 _prevPosition;

        ///// ISurfControllable Implementation /////
        public MoveType moveType { get { return MoveType.Walk; } }
        public MovementConfig moveConfig { get { return movementConfig; } }
        public MoveData moveData { get { return _localMoveData; } }

        public new Collider collider { get { return _collider; } }
        public GameObject groundObject
        {
            get { return _groundObject; }
            set { _groundObject = value; }
        }
        public Vector3 baseVelocity { get { return _baseVelocity; } }
        public Vector3 forward { get { return viewTransform != null ? viewTransform.forward : Vector3.forward; } }
        public Vector3 right   { get { return viewTransform != null ? viewTransform.right   : Vector3.right;   } }
        public Vector3 up      { get { return viewTransform != null ? viewTransform.up      : Vector3.up;      } }

        // Debug: Draw bounding box
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(transform.position, colliderSize);
        }

        private void Awake()
        {
            // Assign references for the controller
            _controller.playerTransform = playerRotationTransform;
            if (viewTransform != null)
            {
                _controller.camera = viewTransform;
                _controller.cameraYPos = viewTransform.localPosition.y;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // If this is not your character, you may want to disable the local camera, etc.
            if (!IsOwner && viewTransform != null)
            {
                // Example: 
                // var cam = viewTransform.GetComponent<Camera>();
                // if (cam) cam.enabled = false;
            }
        }

        private void Start()
        {
            // Create a separate child object for the player's "physical" collider
            _colliderObject = new GameObject("PlayerCollider");
            _colliderObject.layer = gameObject.layer;
            _colliderObject.transform.SetParent(transform);
            _colliderObject.transform.rotation = Quaternion.identity;
            _colliderObject.transform.localPosition = Vector3.zero;
            _colliderObject.transform.SetSiblingIndex(0);

            // Water check object
            _cameraWaterCheckObject = new GameObject("Camera water check");
            _cameraWaterCheckObject.layer = gameObject.layer;
            if (viewTransform != null)
            {
                _cameraWaterCheckObject.transform.position = viewTransform.position;
            }

            var sphere = _cameraWaterCheckObject.AddComponent<SphereCollider>();
            sphere.radius = 0.1f;
            sphere.isTrigger = true;

            var cameraCheckRb = _cameraWaterCheckObject.AddComponent<Rigidbody>();
            cameraCheckRb.useGravity = false;
            cameraCheckRb.isKinematic = true;

            _cameraWaterCheck = _cameraWaterCheckObject.AddComponent<CameraWaterCheck>();

            _prevPosition = transform.position;

            // If no view transform is provided, fallback to main camera
            if (viewTransform == null && Camera.main != null)
            {
                viewTransform = Camera.main.transform;
            }
            if (playerRotationTransform == null && transform.childCount > 0)
            {
                playerRotationTransform = transform.GetChild(0);
            }

            // Remove any existing collider on the main object
            _collider = GetComponent<Collider>();
            if (_collider != null)
            {
                Destroy(_collider);
            }

            // We still add a rigidbody for triggers (e.g., water, etc.), but it’s client-side only now
            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
            {
                _rb = gameObject.AddComponent<Rigidbody>();
            }
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.angularDamping = 0f;
            _rb.linearDamping = 0f;
            _rb.mass = weight;

            allowCrouch = crouchingEnabled;

            // Create the actual collider
            switch (collisionType)
            {
                case ColliderType.Box:
                    var boxC = _colliderObject.AddComponent<BoxCollider>();
                    boxC.size = colliderSize;
                    defaultHeight = boxC.size.y;
                    _collider = boxC;
                    break;

                case ColliderType.Capsule:
                    var capC = _colliderObject.AddComponent<CapsuleCollider>();
                    capC.height = colliderSize.y;
                    capC.radius = colliderSize.x * 0.5f;
                    defaultHeight = capC.height;
                    _collider = capC;
                    break;
            }

            // If the collider is not solid, set isTrigger = true
            if (!solidCollider)
            {
                _collider.isTrigger = true;
            }

            // Initialize local MoveData
            _localMoveData.slopeLimit = movementConfig.slopeLimit;
            _localMoveData.rigidbodyPushForce = rigidbodyPushForce;
            _localMoveData.slidingEnabled = slidingEnabled;
            _localMoveData.laddersEnabled = laddersEnabled;
            _localMoveData.angledLaddersEnabled = supportAngledLadders;
            _localMoveData.playerTransform = transform;
            _localMoveData.viewTransform = viewTransform;
            if (viewTransform != null)
            {
                _localMoveData.viewTransformDefaultLocalPos = viewTransform.localPosition;
            }
            _localMoveData.defaultHeight = defaultHeight;
            _localMoveData.crouchingHeight = crouchingHeightMultiplier;
            _localMoveData.crouchingSpeed = crouchingSpeed;
            _localMoveData.useStepOffset = useStepOffset;
            _localMoveData.stepOffset = stepOffset;
            _localMoveData.origin = transform.position;

            // Remember our start position for later resets
            _startPosition = transform.position;
        }

        private void Update()
        {
            // Only the owner should read local inputs
            if (!IsOwner)
            {
                return;
            }

            // --- Gather local input ---
            float vertical     = Input.GetAxisRaw("Vertical");
            float horizontal   = Input.GetAxisRaw("Horizontal");
            bool sprinting     = Input.GetButton("Sprint");
            bool jumpPressed   = Input.GetButton("Jump");
            bool crouchHeld    = Input.GetButton("Crouch");
            bool jumpDown      = Input.GetButtonDown("Jump");   // if needed
            bool crouchDown    = Input.GetButtonDown("Crouch"); // if needed

            // If you have a separate PlayerAiming script that sets pitch & yaw:
            var playerAiming = GetComponent<PlayerAiming>();
            Vector3 currentViewAngles = playerAiming != null 
                ? playerAiming.RealRotation 
                : Vector3.zero;

            // Store these in our local MoveData
            _localMoveData.viewAngles = currentViewAngles;
            _localMoveData.verticalAxis = vertical;
            _localMoveData.horizontalAxis = horizontal;
            _localMoveData.wishJump = jumpPressed;
            _localMoveData.crouching = crouchHeld;
            _localMoveData.sprinting = sprinting;

            // Convert axes to movement
            if (!Mathf.Approximately(horizontal, 0f))
            {
                _localMoveData.sideMove = horizontal > 0f 
                    ? movementConfig.acceleration 
                    : -movementConfig.acceleration;
            }
            else
            {
                _localMoveData.sideMove = 0f;
            }
            if (!Mathf.Approximately(vertical, 0f))
            {
                _localMoveData.forwardMove = vertical > 0f
                    ? movementConfig.acceleration
                    : -movementConfig.acceleration;
            }
            else
            {
                _localMoveData.forwardMove = 0f;
            }

            // We do the actual movement in FixedUpdate. 
            // But we could also do it in Update if you prefer.
        }

        private void FixedUpdate()
        {
            // Only the owner moves itself
            if (!IsOwner)
            {
                return;
            }

            // 1) Update environment checks (triggers/water)
            HandleWaterAndTriggers();

            // 2) Crouch logic if allowed
            if (allowCrouch)
            {
                _controller.Crouch(this, movementConfig, Time.fixedDeltaTime);
            }

            // 3) Process movement (client side).
            _controller.ProcessMovement(this, movementConfig, Time.fixedDeltaTime);

            // 4) Update the real transform from localMoveData
            transform.position = _localMoveData.origin;

            // If you want to update transform.rotation based on `viewAngles.y`,
            // you can do it here. Example:
            if (playerRotationTransform != null)
            {
                // Only rotate the body around Y axis:
                float yaw = _localMoveData.viewAngles.y;
                playerRotationTransform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        /// <summary>
        /// Checks triggers like water, updates _underwater if needed.
        /// Now fully local on the client.
        /// </summary>
        private void HandleWaterAndTriggers()
        {
            // Re-check triggers
            if (_numberOfTriggers != _triggers.Count)
            {
                _numberOfTriggers = _triggers.Count;
                _underwater = false;
                _triggers.RemoveAll(item => item == null);

                foreach (Collider trigger in _triggers)
                {
                    if (trigger == null) 
                        continue;
                    
                    if (trigger.GetComponentInParent<Water>())
                    {
                        _underwater = true;
                    }
                }
            }

            // Move the "camera water check" sphere to the camera's position
            if (viewTransform != null)
            {
                _cameraWaterCheckObject.transform.position = viewTransform.position;
            }

            _localMoveData.underwater = _underwater;
            _localMoveData.cameraUnderwater = _cameraWaterCheck.IsUnderwater();
        }

        private void OnTriggerEnter(Collider other)
        {
            // Because this is client-side, we can track triggers here
            if (!_triggers.Contains(other))
            {
                _triggers.Add(other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (_triggers.Contains(other))
            {
                _triggers.Remove(other);
            }
        }

        /// <summary>
        /// Locally handle collisions to push the player slightly, if desired.
        /// This is purely local. The server is not enforcing it.
        /// </summary>
        private void OnCollisionStay(Collision collision)
        {
            if (collision.rigidbody == null)
                return;

            // Calculate push force based on relative mass/velocity
            Vector3 relativeVelocity = collision.relativeVelocity * collision.rigidbody.mass / 50f;
            Vector3 impactVelocity = new Vector3(
                relativeVelocity.x * 0.0025f,
                relativeVelocity.y * 0.00025f,
                relativeVelocity.z * 0.0025f);

            float maxYVel = Mathf.Max(_localMoveData.velocity.y, 10f);
            Vector3 newVelocity = new Vector3(
                _localMoveData.velocity.x + impactVelocity.x,
                Mathf.Clamp(
                    _localMoveData.velocity.y + Mathf.Clamp(impactVelocity.y, -0.5f, 0.5f),
                    -maxYVel,
                    maxYVel),
                _localMoveData.velocity.z + impactVelocity.z
            );

            newVelocity = Vector3.ClampMagnitude(newVelocity, Mathf.Max(_localMoveData.velocity.magnitude, 30f));
            _localMoveData.velocity = newVelocity;
        }

        /// <summary>
        /// Reset the player's position and velocity locally. 
        /// Since we have client authority, only the owner can do this.
        /// If the server wants to force a reset, it can send a ClientRpc that calls this on the owner.
        /// </summary>
        public void Reset()
        {
            if (!IsOwner) 
                return;

            // Reset movement data
            _localMoveData.velocity = Vector3.zero;
            _localMoveData.origin = _startPosition;
            _localMoveData.viewAngles = Vector3.zero;
            _localMoveData.crouching = false;
            _localMoveData.sprinting = false;

            // Reset transforms
            transform.position = _startPosition;
            transform.rotation = Quaternion.identity;

            if (playerRotationTransform != null)
            {
                playerRotationTransform.localRotation = Quaternion.identity;
            }
            if (viewTransform != null)
            {
                viewTransform.localPosition = _localMoveData.viewTransformDefaultLocalPos;
                viewTransform.localRotation = Quaternion.identity;
            }

            // Because we're using ClientNetworkTransform (client-authoritative),
            // we can call Teleport() from the client side:
            GetComponent<ClientNetworkTransform>().Teleport(
                _startPosition,
                Quaternion.identity,
                Vector3.one
            );
        }

        /// <summary>
        /// Clamps an angle within a range [from, to].
        /// </summary>
        public static float ClampAngle(float angle, float from, float to)
        {
            if (angle < 0f) angle += 360f;
            if (angle > 180f) return Mathf.Max(angle, 360f + from);
            return Mathf.Min(angle, to);
        }

        [ClientRpc]
        public void ResetClientRpc(Vector3 spawnPosition, Quaternion spawnRotation)
        {
            if (!IsOwner)
                return;

            // Reset movement data
            _localMoveData.velocity = Vector3.zero;
            _localMoveData.origin = spawnPosition;
            _localMoveData.viewAngles = Vector3.zero;
            _localMoveData.crouching = false;
            _localMoveData.sprinting = false;

            // Reset transforms
            transform.position = spawnPosition;
            transform.rotation = spawnRotation;

            if (playerRotationTransform != null)
            {
                playerRotationTransform.localRotation = Quaternion.identity;
            }
            if (viewTransform != null)
            {
                viewTransform.localPosition = _localMoveData.viewTransformDefaultLocalPos;
                viewTransform.localRotation = Quaternion.identity;
            }

            // Teleport via ClientNetworkTransform
            GetComponent<ClientNetworkTransform>().Teleport(
                spawnPosition,
                spawnRotation,
                Vector3.one
            );
        }
    }
}