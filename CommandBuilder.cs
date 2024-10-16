using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRC_138_Remote_Control
{
    class CommandBuilder
    {

        private static readonly byte[] CR = { 0x0D };
        private static readonly byte[] LF = { 0x0A };
        private static readonly byte[] CRLF = { 0x0D, 0x0A };
        private static readonly byte[] Space = { 0x20 };
        private static readonly byte[] GreaterThan = { 0x3E };

        public static byte[] CreateCommand(string first)
        {
            return ByteArrayTools.Combine(System.Text.Encoding.UTF8.GetBytes(first), CR);            
        }
        /*
        public static byte[] CreateCommand(string first, string second)
        {
           return ByteArrayBuilder.Combine(System.Text.Encoding.UTF8.GetBytes(first), ByteArrayBuilder.Combine(System.Text.Encoding.UTF8.GetBytes(second)), CR);
        }

        public static byte[] CreateCommand(string first, string second, string third)
        {
            return ByteArrayBuilder.Combine(System.Text.Encoding.UTF8.GetBytes(first), ByteArrayBuilder.Combine(System.Text.Encoding.UTF8.GetBytes(second)), ByteArrayBuilder.Combine(System.Text.Encoding.UTF8.GetBytes(third)), CR);
        }

        */

        public static byte[] CreateCommand(List<string> commands)
        {
            byte[] ret = System.Text.Encoding.UTF8.GetBytes(commands[0]); //Load the first part of the command into a byte array.

            int index = 0;
                        
            foreach (string s in commands)
            {
                if (index > 0)          //If there is only one part to the command, skip this stuff and go right to appending the carriage return.
                {
                    ret = ByteArrayTools.Combine(ret, Space, System.Text.Encoding.UTF8.GetBytes(s));
                    index++;
                }

                index++;
            }

            return ByteArrayTools.Combine(ret, CR);       //Add the carriage return to the end and return the fully constructed command as a byte array.


        }
    }
}
