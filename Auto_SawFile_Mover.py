import tkinter as tk
from tkinter import filedialog
import os
import shutil
import time
import configparser
import datetime

version = "1.1.20"
timer = 300 # Set the timer to 30 seconds

class App:
    def __init__(self, master):
        self.master = master
        master.title("Auto SawFile Mover v{version}".format(version=version))

        # Add a label to show the source folder
        self.label_source = tk.Label(master, text="Source folder:")
        self.label_source.grid(row=0, column=0)

        # Add an entry to show the source folder
        self.entry_source = tk.Entry(master, text="")
        self.entry_source.grid(row=0, column=1, columnspan=1)

        # Add a button to browse for the source folder
        self.button_browse_source = tk.Button(master, text="Browse", command=self.browse_source)
        self.button_browse_source.grid(row=0, column=3)

        # Add a label to show the target folder
        self.label_target = tk.Label(master, text="Target folder:")
        self.label_target.grid(row=1, column=0)

        # Add an entry to show the target folder
        self.entry_target = tk.Entry(master, text="")
        self.entry_target.grid(row=1, column=1, columnspan=1)

        # Add a button to browse for the target folder
        self.button_browse_target = tk.Button(master, text="Browse", command=self.browse_target)
        self.button_browse_target.grid(row=1, column=3)

        # Add a button to stop the batch file
        self.button_stop = tk.Button(master, text="Stop", command=self.stop_batch_file)
        self.button_stop.grid(row=2, column=0)

        # Add a label to show the remaining time until auto-start
        self.label_timer = tk.Label(master, text="")
        self.label_timer.grid(row=2, column=1, columnspan=2)

        # Set the auto-start timer
        self.auto_start_timer = timer
        self.update_timer()

        # Load the settings file
        self.settings = configparser.ConfigParser()
        self.load_settings()
        # Add in a Log File System
        self.log_file()

        # Add label to show the status
        self.label_status = tk.Label(master, text="Status: Idle")
        self.label_status.grid(row=4, column=0, columnspan=4)

        # Add a label to show the warning
        self.label_warning = tk.Label(master, text="Warning: File moving in progress. DO NOT CLOSE THIS WINDOW")
        self.label_warning.grid(row=5, column=0, columnspan=4)

        # Add a label to show who made this
        self.label_made_by = tk.Label(master, text="Powered by: Saw Dust and Unicorn Tears")
        self.label_made_by.grid(row=6, column=0, columnspan=4)

        self.stop_flag = False

    def load_settings(self):
        self.settings = configparser.ConfigParser()
        if os.path.isfile('settings.ini'):
            self.settings.read('settings.ini')
        else:
            self.settings['FOLDERS'] = {'source_folder': '', 'target_folder': ''}
            self.save_settings()
        try:
            self.entry_source.delete(0, tk.END)
            self.entry_source.insert(0, self.settings.get('FOLDERS', 'source_folder'))
            self.entry_target.delete(0, tk.END)
            self.entry_target.insert(0, self.settings.get('FOLDERS', 'target_folder'))
        except Exception as e:
            print(f"Error loading settings: {e}")
            
    def save_settings(self):
        with open('settings.ini', 'w') as configfile:
            self.settings.write(configfile)

    # Create a Logging File everyday to keep track of the files moved
    def log_file(self):
        # If folder does not exist, create it
        if not os.path.exists("Logs"):
            os.makedirs("Logs")

        # Get the current date
        now = datetime.datetime.now()
        date = now.strftime("%Y-%m-%d")

        # Create a new log file
        self.log = open(f"Logs/{date}.txt", "a+") # a+ means append and create if not exist
        self.log.write(f"Log File for {date}\n")
        self.log.write("Source Folder: " + self.entry_source.get() + "\n")
        self.log.write("Target Folder: " + self.entry_target.get() + "\n")
        self.log.write("--------------------------------------------------\n")
        self.log.close()

    def update_timer(self):
        # Update the label to show the remaining time
        self.label_timer.configure(text=f"Auto-starting in {self.auto_start_timer} seconds")
        if self.auto_start_timer >= 0:
            self.auto_start_timer -= 1 # Decrement the timer
            self.master.after(1000, self.update_timer)  # If the timer is not finished, schedule the update again after 1 second
        else:
            self.execute_batch_file() # If the timer is finished, start the batch file
    
    def browse_source(self):
        # Override Preset
        folder_path = filedialog.askdirectory()
        self.entry_source.delete(0, tk.END)
        self.entry_source.insert(0, folder_path)

        # Save settings to file
        self.settings.set('FOLDERS', 'source_folder', folder_path)
        self.save_settings()

    def browse_target(self):
        # Override Preset
        folder_path = filedialog.askdirectory()
        self.entry_target.delete(0, tk.END)
        self.entry_target.insert(0, folder_path)

        # Save settings to file
        self.settings.set('FOLDERS', 'target_folder', folder_path)
        self.save_settings()


    def execute_batch_file(self):
        self.stop_flag = False
        source_folder = self.entry_source.get()
        target_folder = self.entry_target.get()
        self.label_status.configure(text="Status: Running") # Update the status label
        time.sleep(5) # Wait 5 second

        newFolderCreated = False
        newFolderName = ""

        while True:
            if self.stop_flag:
                break

            try:
                file_list = os.listdir(source_folder)
            except FileNotFoundError:
                # DEBUG: print("1. No files found in source folder")
                # Add logging to log file
                date = datetime.datetime.now().strftime("%Y-%m-%d")
                self.log = open(f"Logs/{date}.txt", "a+")
                self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - No files found in source folder\n")
                self.log.close()
                self.label_status.configure(text="Status: Awaiting new files") # Update the status label
                self.auto_start_timer = timer # Reset the timer
                self.update_timer()
                break

            if not file_list:
                # DEBUG: print("2. No files found in source folder")
                # add logging to log file
                date = datetime.datetime.now().strftime("%Y-%m-%d")
                self.log = open(f"Logs/{date}.txt", "a+")
                self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - No files found in source folder\n")
                self.log.close()
                self.label_status.configure(text="Status: Awaiting new files") # Update the status label
                self.auto_start_timer = timer # Reset the timer
                self.update_timer()
                break
            
            for file in os.listdir(source_folder):
                # DEBUG: print(f"Checking file: {file}")
                filePath = os.path.join(source_folder, file)
                if os.path.isfile(filePath):
                    if not newFolderCreated:
                        newFolderName = os.path.splitext(file)[0][:-3]
                        if os.path.exists(os.path.join(target_folder, newFolderName)):
                            newFolder = os.path.join(target_folder, newFolderName)
                            shutil.move(filePath, os.path.join(newFolder, file))
                            # DEBUG: print(f"Moved file to EXISTING: {file} to {newFolder}")
                            #Add logging to log file
                            date = datetime.datetime.now().strftime("%Y-%m-%d")
                            self.log = open(f"Logs/{date}.txt", "a+")
                            self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - Moved file to EXISTING: {file} to {newFolder}\n")
                            self.log.close()
                            newFolderName = ""
                            break
                        else:
                            newFolder = os.path.join(source_folder, newFolderName)
                            os.makedirs(newFolder)
                            newFolderCreated = True
                            # DEBUG: print(f"Created folder: {newFolder}")
                    try:
                        shutil.move(filePath, os.path.join(newFolder, file))
                        if os.path.splitext(file)[0][:-3] == newFolderName:
                            # DEBUG: print(f"Moved file to NEW: {file} to {newFolder}")
                            self.label_status.configure(text=f"Status: Moved file to NEW") # update the status label
                            time.sleep(2)
                        else:
                            shutil.move(os.path.join(newFolder, file), source_folder)
                    except Exception as e:
                        # DEBUG: print(f"Error moving file: {filePath}")
                        # Add logging to log file
                        date = datetime.datetime.now().strftime("%Y-%m-%d")
                        self.log = open(f"Logs/{date}.txt", "a+")
                        self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - Error moving file: {filePath}\n")
                        self.log.close()
                        self.label_status.configure(text=f"Status: Error moving file") # update the status label
            if newFolderCreated:
                try:
                    shutil.move(newFolder, os.path.join(target_folder, newFolderName))
                    # DEBUG: print(f"Moved folder to target: {os.path.join(target_folder, newFolderName)}")
                    # Add logging to log file
                    date = datetime.datetime.now().strftime("%Y-%m-%d")
                    self.log = open(f"Logs/{date}.txt", "a+")
                    self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - Moved folder to target: {os.path.join(target_folder, newFolderName)}\n")
                    self.log.close()
                    newFolderCreated = False
                    newFolderName = ""
                except Exception as e:
                    # DEBUG: print(f"Error moving folder: {newFolder}")
                    # Add logging to log file
                    date = datetime.datetime.now().strftime("%Y-%m-%d")
                    self.log = open(f"Logs/{date}.txt", "a+")
                    self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - Error moving folder: {newFolder}\n")
                    self.log.close()
                    self.label_status.configure(text=f"Status: Error moving folder") # update the status label

    def stop_batch_file(self):
        self.stop_flag = True # Set the stop flag to True
        self.label_status.configure(text="Status: Stopped for 10 mins") # Update the status label
        # Add logging to log file
        date = datetime.datetime.now().strftime("%Y-%m-%d")
        self.log = open(f"Logs/{date}.txt", "a+")
        self.log.write(f"{datetime.datetime.now().strftime('%H:%M:%S')} - Stopped for 10 mins\n")
        self.log.close()
        self.auto_start_timer = 600 # Reset the timer
        # DEBUG: print("Stopping batch file")

root = tk.Tk()
app = App(root)

root.mainloop()