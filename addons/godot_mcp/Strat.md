# Okay what is the goal here ? 
To get the outputs and inputs from one class or something larger than a class that allows more branches and allows it tobe called and used externally.
---

# What are those inputs and outputs?
1. The Users responce.
2. The A.Is output.
3. Calling History.
4. inataracting with the chat logs

---

# How will that user input work?
I assume something like a **Method** `public UserInput (string User_Input)` , that method or whatever fits should initiate plawright,it should also support async.
it should also set up  the automation code for sending a message.
> Tho there is a tiny issue, have tho initiate playwright on each method would be problematic,
> So why dont we initiate it either by setting up a method that does so and calling it at the start of each method that will be called externally
> Or we set it up on a class level , maybe each method already has it initiated ? 

---

# What should one of those methods look like?

 So first we have a input method that takes in UserInput, or maybe we make it a OOP higher up 
 so that it takes in user input yes but its output varies , okay thats a given, but in what way?
 it output isnt just strings put processes too, or the method does those processes and gieves out a string? 
 so first it checks if the `PlayWrightProfile` folder is empty , 
 if yes set the output to prompt the user to login into google by setting the users output to return it as a string , so if the user types something in ,
 it goes into the method `UserInput()` the method initialses playwright, checks the state of the folder with an if, returns a string telling the user to login
 Open up the browser , waits for the user to login, wrap it in atry catch just in case theres a time out or the user does something wrong that prompts the user to try again later
 Now the usre logs in, the state gets saved and they can send the next message
> // some sample draft code/psudocode
>  public UserInput(string User_Input){ 
        User_Input;
      // assuming we are using the method style , until we get something better
      initialisePlayeright();
        var Statepath = PlaywrightPath;
        if(Statepath ==! null){
            return "LogIn First";
            try{
                page.GotoAsync("https://accounts.google.com/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 1200000
                }).GetAwaiter().GetResult();
                return "Login"
            }catch(TimeOutExceptiion){
                return "Try again";
            }
            return "Login Succesfull";
        
        
                

        }
    }

 **After that state check whats next**
 well the userInput method seems to have alot of ouputs or can send alot of outputs from one input, will that work? ill the outputs or can the outputs be sent out as a chain? 
 if lets say its called once in an external terminal or ui, ofcourse in a terminal it would work, but deoends on the ui, wait how do i do taht even in the terminal ,or whatver ui? by spamming return in place of something like console.writeline?? 