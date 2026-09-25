namespace SoD2SE
{
    // Single source for every shortcut key the MCM accepts.
    //
    // The native host applies the same rule from mcm::Shortcut* in
    // Native/McmProtocol.h, and verify_protocol.py compares the two tables, so
    // a key the menu can record can never be rejected by the C# side (or the
    // other way round).  verify_sources.py keeps the F1/F2 defaults from being
    // spelled out again in Core/Mcm.cs or the loader overlay.
    public static class McmKeys
    {
        // Function, letter, digit and navigation windows.  The numbers are the
        // Windows virtual-key codes the native side sees; the names exist so a
        // bare 112 or 123 cannot appear at a call site.
        public const int FunctionKeyFirst = 112, FunctionKeyLast = 123;   // VK_F1..VK_F12
        public const int LetterKeyFirst = 65, LetterKeyLast = 90;         // VK_A..VK_Z
        public const int DigitKeyFirst = 48, DigitKeyLast = 57;           // VK_0..VK_9
        public const int NavigationKeyFirst = 33, NavigationKeyLast = 36; // VK_PRIOR..VK_HOME
        public const int InsertKey = 45, DeleteKey = 46, TabKey = 9;      // VK_INSERT/VK_DELETE/VK_TAB
        // VK_F4: the OS keeps it, and the game only survives it with Alt held.
        public const int ReservedKey = FunctionKeyFirst + 3;

        public const int ControlModifier = 1, AltModifier = 2, ShiftModifier = 4, MaxModifiers = 7;

        // Defaults: F1 opens the settings menu, F2 asks for the upgrade prompt.
        public const int MenuKey = FunctionKeyFirst;
        public const int MenuModifiers = 0;
        public const int ChoiceKey = FunctionKeyFirst + 1;
        public const int ChoiceModifiers = 0;

        public static bool IsFunctionKey(int key)
        {
            return key >= FunctionKeyFirst && key <= FunctionKeyLast;
        }

        public static int FunctionKeyNumber(int key)
        {
            return key - FunctionKeyFirst + 1;
        }

        public static int FunctionKey(int number)
        {
            return FunctionKeyFirst + number - 1;
        }

        public static int NavigationKeyNumber(int key)
        {
            return key - NavigationKeyFirst + 1;
        }

        // The exact rule the native host applies before it forwards a recorded
        // key.  Keeping it here means the recorder and the validator cannot
        // disagree about what counts as a usable shortcut.
        public static bool ValidKey(int key, int modifiers)
        {
            return modifiers >= 0 && modifiers <= MaxModifiers &&
                (IsFunctionKey(key) ||
                 (key >= LetterKeyFirst && key <= LetterKeyLast) ||
                 (key >= DigitKeyFirst && key <= DigitKeyLast) ||
                 (key >= NavigationKeyFirst && key <= NavigationKeyLast) ||
                 key == InsertKey || key == DeleteKey || key == TabKey) &&
                !(key == ReservedKey && (modifiers & AltModifier) != 0);
        }

        // Mod screens start right after the upgrade prompt: F2, F3, ... used to
        // be a hard-coded array in Core/Ui.cs.
        public static int[] DefaultSurfaceKeys(int count)
        {
            var keys = new int[count < 0 ? 0 : count];
            for (int index = 0; index < keys.Length; index++)
                keys[index] = FunctionKey(2 + index);
            return keys;
        }
    }
}
