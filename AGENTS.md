# Current file contains common CODE GENERATION rules to keep general code styling:
- Local variables always start from "v_" prefix
- Method parameters always start from "p_" prefix
- Class private members always start from "m_" prefix
- Consts always start from "CONST_" prefix
- Enums always start from "Enum" prefix
- Classes always start from "C" prefix
- Split long conditions into multiple lines, e.g.:
```csharp
if (some_very_ver_very_very_very_long_condition1 
	&& (some_very_ver_very_very_very_long_condition2 
		|| some_very_ver_very_very_very_long_condition3) 
	&& some_very_ver_very_very_very_long_condition4)
{
	// code
}
```
- Code blocks always start on new line, block content also starts on new line, closing brace also on new line. E.g.:
```if (condition)
{
	// code
}

while (condition)
{
	// code
}

using (var resource = new Resource())
{
	// code
}

try
{
	// code
}
catch (Exception ex)
{
	// code
}

variable = new SomeClass()
{
	Property1 = value1,
	Property2 = value2
};
```
- Use comments to describe code blocks, e.g.:
```
// This is a comment describing the following code block
if (condition)
{
	// code
}
```