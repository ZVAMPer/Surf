using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class WeaponManager : MonoBehaviour
{
    public WeaponBase[] weapons;
    private int currentWeaponIndex = 0;
    public TMP_Text weaponNameText; // Reference to the UI Text component

    void Start()
    {
        EquipWeapon(0);
    }

    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            EquipWeapon(0);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            EquipWeapon(1);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            EquipWeapon(2);
        }

        if (Input.GetMouseButtonDown(0))
        {
            weapons[currentWeaponIndex].UseWeapon();
        }
    }

    void EquipWeapon(int index)
    {
        if (index < 0 || index >= weapons.Length) return;

        for (int i = 0; i < weapons.Length; i++)
        {
            weapons[i].gameObject.SetActive(i == index);
        }

        currentWeaponIndex = index;
        UpdateWeaponNameUI();
    }

    void UpdateWeaponNameUI()
    {
        if (weaponNameText != null)
        {
            weaponNameText.text = weapons[currentWeaponIndex].weaponName;
        }
    }
}