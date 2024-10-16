using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PRC_138_Remote_Control;

namespace ExtensionMethods
{

   public static class EnumEx
    {
        /*
        public static string GetDescription(this Enum value)
        {
            FieldInfo field = value.GetType().GetField(value.ToString());

            DescriptionAttribute attribute
                    = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute))
                        as DescriptionAttribute;

            return attribute == null ? value.ToString() : attribute.Description;
        }
        public static string[] GetDescriptions(this Enum value)
        {
            StringBuilder sb = new StringBuilder();
            List<string> list = new List<string>();
            
            
            FieldInfo field = value.GetType().GetField(value.ToString());

            DescriptionAttribute attribute
                    = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute))
                        as DescriptionAttribute;

            return attribute == null ? value.ToString() : attribute.Description;
        }
        */

        // This extension method is broken out so you can use a similar pattern with 
        // other MetaData elements in the future. This is your base method for each.
        private static T GetAttribute<T>(this Enum value) where T : Attribute
        {
            var type = value.GetType();
            var memberInfo = type.GetMember(value.ToString());
            var attributes = memberInfo[0].GetCustomAttributes(typeof(T), false);
            return attributes.Length > 0
              ? (T)attributes[0]
              : null;
        }

        // This method creates a specific call to the above method, requesting the
        // Description MetaData attribute.
        public static string ToDescription(this Enum value)
        {
            var attribute = value.GetAttribute<DescriptionAttribute>();
            return attribute == null ? value.ToString() : attribute.Description;
        }
        public static string[] GetDescriptions(Type type)
        {
            var descs = new List<string>();
            var names = Enum.GetNames(type);
            foreach (var name in names)
            {
                var field = type.GetField(name);
                var fds = field.GetCustomAttributes(typeof(DescriptionAttribute), true);
                foreach (DescriptionAttribute fd in fds)
                {
                    descs.Add(fd.Description);
                }
            }
            return descs.ToArray();
        }

        public static T GetValueFromDescription<T>(string description)
        {
            var type = typeof(T);
            if (!type.IsEnum) throw new InvalidOperationException();
            foreach (var field in type.GetFields())
            {
                var attribute = Attribute.GetCustomAttribute(field,
                    typeof(DescriptionAttribute)) as DescriptionAttribute;
                if (attribute != null)
                {
                    if (attribute.Description == description)
                        return (T)field.GetValue(null);
                }
                else
                {
                    if (field.Name == description)
                        return (T)field.GetValue(null);
                }
            }
            throw new Exception("Unrecognized Message: '" + description + "'");
            // or return default(T);
        }
        public static T GetValueFromDescription<T>(FalconRadio.Responses caller, string description)
        {
            var type = typeof(T);
            if (!type.IsEnum) throw new InvalidOperationException();
            foreach (var field in type.GetFields())
            {
                var attribute = Attribute.GetCustomAttribute(field,
                    typeof(DescriptionAttribute)) as DescriptionAttribute;
                if (attribute != null)
                {
                    if (attribute.Description == description)
                        return (T)field.GetValue(null);
                }
                else
                {
                    if (field.Name == description)
                        return (T)field.GetValue(null);
                }
            }
            throw new Exception("Unrecognized Message: " + caller.ToString() + " - '" + description + "'");
            // or return default(T);
        }
    }
    public partial class NumericUpDownEx : NumericUpDown
    {
        public enum ValueChangedType
        {
            TextEdit,
            UpButton,
            DownButton,
            Programmatic
        }
        public ValueChangedType ChangedType = ValueChangedType.Programmatic;
        public NumericUpDownEx()
        {
            //InitializeComponent();
        }
        public override void UpButton()
        {
            this.ChangedType = ValueChangedType.UpButton;
            base.UpButton();
        }
        public override void DownButton()
        {
            this.ChangedType = ValueChangedType.DownButton;
            base.DownButton();
        }
        protected override void OnLostFocus(EventArgs e)
        {
            this.ChangedType = ValueChangedType.TextEdit;
            base.OnLostFocus(e);
        }
        public void SetValue(decimal val)
        {
            this.ChangedType = ValueChangedType.Programmatic;
            this.Value = val;
        }
    }

    /*
    public class ReadOnlyTextBox : TextBox
    {
        [DllImport("user32.dll")]
        static extern bool HideCaret(IntPtr hWnd);

        public ReadOnlyTextBox()
        {
            this.ReadOnly = true;
            //this.BackColor = Color.White;
            this.GotFocus += TextBoxGotFocus;
            //this.Cursor = Cursors.Arrow; // mouse cursor like in other controls
        }

        private void TextBoxGotFocus(object sender, EventArgs args)
        {
            HideCaret(this.Handle);
        }
    }
    */

}